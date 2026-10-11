using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// describe_elements (SPEC Appendix A): up to 50 elements per page with parameters (values in mm/m²/m³/deg, Yes/No
    /// as booleans, element-id params as ids), writability, type parameters, geometry, relations (host, hosted inserts,
    /// room, joins, group, assembly, sub-components) and MEP connectors. Dimensions always carry their value, segments and
    /// witness references in Revit's stable representation (for annotate op=dimension refs=). Parameter keys are the
    /// English labels of built-ins (any UI language), otherwise the display name; duplicates get a " [bip:X]",
    /// " [guid:...]" or " [id:N]" suffix that set_parameters accepts as is.
    /// </summary>
    internal static class DescribeOps
    {
        public const int MaxPerPage = 50;

        [Op("describe_elements")]
        public static OpResult Describe(RequestContext ctx)
        {
            Document host = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document is open.");
            Document doc = host;
            Transform linkTransform = null;
            string link = ctx.Args.Str("link");
            if (!string.IsNullOrWhiteSpace(link))
            {
                RevitLinkInstance instance = Resolve.Link(host, link, ctx.Args.Field("link"));
                doc = instance.GetLinkDocument() ?? throw new OpException(ErrorCodes.NotFound, "The link '" + instance.Name + "' is not loaded.",
                    new Dictionary<string, object> { ["param"] = "link", ["kind"] = "link", ["value"] = link, ["candidates"] = new List<object>() });
                linkTransform = instance.GetTotalTransform();
            }

            List<long> ids = ReadKit.CombinedSelector(ctx, doc);
            if (ids == null) throw OpException.InvalidArgs("ids", "give ids (element ids) or from ('selection', 'last' or an r# handle)", new Dictionary<string, object> { ["ids"] = new List<object> { "304512" } });
            List<Element> elements = ReadKit.Existing(ctx, doc, ids);
            if (elements.Count == 0)
            {
                throw new OpException(ErrorCodes.NotFound, ids.Count == 0 ? "No elements were given (the selection is empty)." : "None of the elements exist in '" + doc.Title + "'.",
                    new Dictionary<string, object> { ["param"] = ctx.Args.Has("ids") ? "ids" : "from", ["kind"] = "element", ["value"] = string.Join(",", ids.Take(10)), ["candidates"] = new List<object>() });
            }

            string mode = ctx.Args.Enum("params", "writable", "writable", "all", "names", "none");
            var include = new HashSet<string>(ctx.Args.Strs("include").Select(s => s.Trim().ToLowerInvariant()));
            foreach (string unknown in include.Where(i => i != "type" && i != "geometry" && i != "relations" && i != "connectors").ToList())
            {
                throw OpException.InvalidArgs("include", "'" + unknown + "' is not one of type, geometry, relations, connectors");
            }
            string match = ctx.Args.Str("match");
            var options = new DescribeOptions { Mode = mode, Match = match, LinkTransform = linkTransform };

            int offset = ReadKit.Offset(ctx);
            int limit = ReadKit.Limit(ctx, MaxPerPage, MaxPerPage);
            List<Element> page = elements.Skip(offset).Take(limit).ToList();
            var described = new List<object>();
            int index = 0;
            foreach (Element element in page)
            {
                if (ctx.ShouldYield(++index) && described.Count > 0)
                {
                    ctx.Warn(WarningCodes.PartialResult, "stopped at the time budget after " + described.Count.ToString(CultureInfo.InvariantCulture) + " elements");
                    break;
                }
                described.Add(DescribeOne(ctx, element, include, options));
            }

            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["total"] = elements.Count,
                ["units"] = "lengths mm, areas m2, volumes m3, angles deg",
                ["elements"] = described
            };
            if (linkTransform != null) data["link"] = link;
            string summary = described.Count == 1 && elements.Count == 1
                ? Label(page[0])
                : ReadKit.Count(elements.Count, "element") + " (" + string.Join(", ", page.GroupBy(e => Rows.CategoryLabel(e) ?? "?").Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + g.Key)) + ")";
            return OpResult.Success(data, summary).WithPage(elements.Count, offset, described.Count);
        }

        private sealed class DescribeOptions
        {
            public string Mode;
            public string Match;
            public Transform LinkTransform;
        }

        private static string Label(Element element)
        {
            string category = Rows.CategoryLabel(element) ?? "element";
            string type = Rows.TypeName(element);
            return category + " " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(type) ? string.Empty : " (" + type + ")");
        }

        private static Dictionary<string, object> DescribeOne(RequestContext ctx, Element element, HashSet<string> include, DescribeOptions options)
        {
            Document doc = element.Document;
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = element.Id.Value,
                ["category"] = Rows.CategoryLabel(element),
                ["class"] = element.GetType().Name
            };
            string family = Rows.FamilyName(element);
            if (!string.IsNullOrEmpty(family)) result["family"] = family;
            ElementType type = element as ElementType == null ? Rows.TypeOf(element) : null;
            if (type != null)
            {
                result["type"] = type.Name;
                result["type_id"] = type.Id.Value;
            }
            if (Rows.IsNamed(element) || element is ElementType) result["name"] = ReadKit.SafeName(element);
            string level = element is ElementType ? null : Rows.LevelName(element);
            if (!string.IsNullOrEmpty(level)) result["level"] = level;
            result["uid"] = element.UniqueId;
            if (element.Pinned) result["pinned"] = true;

            if (element is Dimension dimension) result["dimension"] = DimensionBlock(dimension);

            if (options.Mode != "none")
            {
                result["params"] = Parameters(element, options);
                if (options.Mode == "all") result["read_only"] = ReadOnlyNames(element, options);
            }
            if (include.Contains("type") && type != null)
            {
                result["type_params"] = Parameters(type, options);
                if (options.Mode == "all") result["type_read_only"] = ReadOnlyNames(type, options);
            }
            if (include.Contains("geometry")) result["geometry"] = GeometryBlock(element, options.LinkTransform);
            if (include.Contains("relations")) result["relations"] = Relations(element);
            if (include.Contains("connectors")) result["connectors"] = Connectors(element, options.LinkTransform);
            return result;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Parameters
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// writable/all: {key: value}; names: {key: "kind rw|ro"}. Keys are unique per element (suffix on duplicates).
        /// </summary>
        private static Dictionary<string, object> Parameters(Element element, DescribeOptions options)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            Func<string, bool> test = ReadKit.NameMatcher(options.Match);
            foreach (KeyValuePair<string, Parameter> pair in Keyed(element))
            {
                Parameter parameter = pair.Value;
                if (!test(pair.Key)) continue;
                bool readOnly = SafeReadOnly(parameter);
                switch (options.Mode)
                {
                    case "writable":
                        if (readOnly) continue;
                        result[pair.Key] = ParamValues.Get(parameter, element.Document);
                        break;
                    case "all":
                        result[pair.Key] = ParamValues.Get(parameter, element.Document);
                        break;
                    case "names":
                        result[pair.Key] = KindName(parameter) + (readOnly ? " ro" : " rw");
                        break;
                }
            }
            return result;
        }

        private static List<object> ReadOnlyNames(Element element, DescribeOptions options)
        {
            Func<string, bool> test = ReadKit.NameMatcher(options.Match);
            return Keyed(element).Where(p => test(p.Key) && SafeReadOnly(p.Value)).Select(p => (object)p.Key).ToList();
        }

        /// <summary>The element's parameters keyed by English label / display name, sorted, duplicates disambiguated.</summary>
        internal static List<KeyValuePair<string, Parameter>> Keyed(Element element)
        {
            var list = new List<KeyValuePair<string, Parameter>>();
            if (element == null) return list;
            var parameters = new List<Parameter>();
            try
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter?.Definition != null) parameters.Add(parameter);
                }
            }
            catch
            {
                return list;
            }
            var byKey = parameters.GroupBy(p => ParamValues.KeyOf(p), StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, Parameter> group in byKey)
            {
                if (group.Count() == 1)
                {
                    list.Add(new KeyValuePair<string, Parameter>(group.Key, group.First()));
                    continue;
                }
                foreach (Parameter parameter in group) list.Add(new KeyValuePair<string, Parameter>(group.Key + " [" + Qualifier(parameter) + "]", parameter));
            }
            return list.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Qualifier(Parameter parameter)
        {
            BuiltInParameter? bip = ParamValues.BuiltInOf(parameter);
            if (bip.HasValue) return "bip:" + bip.Value;
            try
            {
                if (parameter.IsShared) return "guid:" + parameter.GUID.ToString();
            }
            catch
            {
                // Not shared.
            }
            return "id:" + parameter.Id.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static bool SafeReadOnly(Parameter parameter)
        {
            try { return parameter.IsReadOnly; } catch { return true; }
        }

        private static string KindName(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String: return "text";
                    case StorageType.ElementId: return "id";
                    case StorageType.Integer: return ParamValues.IsYesNo(parameter) ? "yes/no" : "integer";
                    case StorageType.Double:
                        switch (Units.KindOf(ParamValues.SpecOf(parameter)))
                        {
                            case ValueKind.Length: return "length mm";
                            case ValueKind.Area: return "area m2";
                            case ValueKind.Volume: return "volume m3";
                            case ValueKind.Angle: return "angle deg";
                            case ValueKind.Number: return "number";
                            default: return ReadKit.ShortTypeId(ParamValues.SpecOf(parameter)) ?? "number";
                        }
                    default: return "none";
                }
            }
            catch
            {
                return "?";
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Dimensions
        // ---------------------------------------------------------------------------------------------------------

        private static Dictionary<string, object> DimensionBlock(Dimension dimension)
        {
            Document doc = dimension.Document;
            var block = new Dictionary<string, object>(StringComparer.Ordinal);
            try { block["value"] = dimension.Value.HasValue ? Units.ToOutput(SpecOfDimension(dimension), dimension.Value.Value, doc) : null; } catch { }
            try { block["text"] = dimension.ValueString; } catch { }
            try { block["view"] = dimension.View?.Name; } catch { }
            try
            {
                if (dimension.NumberOfSegments > 1)
                {
                    var segments = new List<object>();
                    foreach (DimensionSegment segment in dimension.Segments)
                    {
                        segments.Add(segment.Value.HasValue ? Units.ToOutput(SpecOfDimension(dimension), segment.Value.Value, doc) : null);
                    }
                    block["segments"] = segments;
                }
            }
            catch
            {
                // No segments.
            }
            var refs = new List<object>();
            var elements = new List<object>();
            try
            {
                foreach (Reference reference in dimension.References)
                {
                    string stable = null;
                    try { stable = reference.ConvertToStableRepresentation(doc); } catch { }
                    refs.Add(stable);
                    long id = reference.ElementId.Value;
                    if (!elements.Contains(id)) elements.Add(id);
                }
            }
            catch
            {
                // Spot dimensions etc. have no reference array.
            }
            block["refs"] = refs;
            block["ref_elements"] = elements;
            try { block["dimension_type"] = ReadKit.SafeName(doc.GetElement(dimension.GetTypeId())); } catch { }
            return block;
        }

        private static ForgeTypeId SpecOfDimension(Dimension dimension)
        {
            try
            {
                if (dimension.DimensionShape == DimensionShape.Angular) return SpecTypeId.Angle;
            }
            catch
            {
                // Linear by default.
            }
            return SpecTypeId.Length;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Geometry
        // ---------------------------------------------------------------------------------------------------------

        private static Dictionary<string, object> GeometryBlock(Element element, Transform linkTransform)
        {
            var block = new Dictionary<string, object>(StringComparer.Ordinal);
            Func<XYZ, XYZ> map = p => linkTransform == null ? p : linkTransform.OfPoint(p);
            switch (element.Location)
            {
                case LocationPoint point:
                    block["point"] = OutputJson.Point(map(point.Point));
                    try { block["rotation"] = Units.Angle(point.Rotation); } catch { }
                    break;
                case LocationCurve curve:
                    Curve c = curve.Curve;
                    block["curve"] = new List<object> { OutputJson.Point(map(c.GetEndPoint(0))), OutputJson.Point(map(c.GetEndPoint(1))) };
                    block["length"] = Units.Mm(c.Length);
                    if (c is Arc arc)
                    {
                        block["arc_center"] = OutputJson.Point(map(arc.Center));
                        block["radius"] = Units.Mm(arc.Radius);
                    }
                    break;
            }
            BoundingBoxXYZ box = Geometry.BoxOf(element);
            if (box != null)
            {
                if (linkTransform != null)
                {
                    XYZ a = linkTransform.OfPoint(box.Transform == null ? box.Min : box.Transform.OfPoint(box.Min));
                    XYZ b = linkTransform.OfPoint(box.Transform == null ? box.Max : box.Transform.OfPoint(box.Max));
                    box = new BoundingBoxXYZ { Min = Geometry.Min(a, b), Max = Geometry.Max(a, b) };
                }
                block["bbox"] = OutputJson.Box(box);
            }
            AddMeasure(block, "area", element, BuiltInParameter.HOST_AREA_COMPUTED, BuiltInParameter.ROOM_AREA);
            AddMeasure(block, "volume", element, BuiltInParameter.HOST_VOLUME_COMPUTED, BuiltInParameter.ROOM_VOLUME);
            if (element is Wall wall)
            {
                try { block["width"] = Units.Mm(wall.Width); } catch { }
                AddMeasure(block, "height", element, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                try { block["orientation"] = OutputJson.Point(wall.Orientation).Take(2).ToList(); } catch { }
                try { block["flipped"] = wall.Flipped; } catch { }
            }
            if (element is FamilyInstance instance)
            {
                try { block["facing"] = OutputJson.Point(instance.FacingOrientation).Take(2).ToList(); } catch { }
                try { block["hand"] = OutputJson.Point(instance.HandOrientation).Take(2).ToList(); } catch { }
                try { if (instance.Mirrored) block["mirrored"] = true; } catch { }
                try { if (instance.FacingFlipped) block["facing_flipped"] = true; } catch { }
                try { if (instance.HandFlipped) block["hand_flipped"] = true; } catch { }
            }
            if (element is SpatialElement spatial)
            {
                try
                {
                    IList<IList<BoundarySegment>> loops = spatial.GetBoundarySegments(new SpatialElementBoundaryOptions());
                    if (loops != null)
                    {
                        var outlines = new List<object>();
                        foreach (IList<BoundarySegment> loop in loops.Take(10))
                        {
                            outlines.Add(loop.Select(s => (object)OutputJson.Point2(map(s.GetCurve().GetEndPoint(0)))).ToList());
                        }
                        block["boundary"] = outlines;
                    }
                }
                catch
                {
                    // Unplaced or unbounded.
                }
            }
            return block;
        }

        private static void AddMeasure(Dictionary<string, object> block, string key, Element element, params BuiltInParameter[] bips)
        {
            foreach (BuiltInParameter bip in bips)
            {
                Parameter parameter;
                try { parameter = element.get_Parameter(bip); } catch { parameter = null; }
                if (parameter == null || !parameter.HasValue) continue;
                block[key] = ParamValues.Get(parameter, element.Document);
                return;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Relations
        // ---------------------------------------------------------------------------------------------------------

        private static Dictionary<string, object> Relations(Element element)
        {
            Document doc = element.Document;
            var block = new Dictionary<string, object>(StringComparer.Ordinal);
            if (element is FamilyInstance instance)
            {
                try { if (instance.Host != null) block["host"] = instance.Host.Id.Value; } catch { }
                try
                {
                    Room room = instance.Room;
                    if (room != null) block["room"] = RoomRef(room);
                    if (instance.FromRoom != null) block["from_room"] = RoomRef(instance.FromRoom);
                    if (instance.ToRoom != null) block["to_room"] = RoomRef(instance.ToRoom);
                }
                catch
                {
                    // Phase-less instance.
                }
                try { if (instance.SuperComponent != null) block["super_component"] = instance.SuperComponent.Id.Value; } catch { }
                try
                {
                    ICollection<ElementId> subs = instance.GetSubComponentIds();
                    if (subs.Count > 0) block["sub_components"] = OutputJson.Ids(subs.Take(50));
                }
                catch
                {
                    // None.
                }
                try
                {
                    if (instance.Space != null) block["space"] = instance.Space.Number;
                }
                catch
                {
                    // No spaces.
                }
            }
            else if (element.Location is LocationPoint || element.Location is LocationCurve)
            {
                Room room = RoomAt(element);
                if (room != null) block["room"] = RoomRef(room);
            }
            if (element is HostObject hostObject)
            {
                try
                {
                    IList<ElementId> inserts = hostObject.FindInserts(true, true, true, true);
                    if (inserts.Count > 0)
                    {
                        block["hosted"] = OutputJson.Ids(inserts.Take(100));
                        if (inserts.Count > 100) block["hosted_total"] = inserts.Count;
                    }
                }
                catch
                {
                    // No inserts.
                }
            }
            try
            {
                ICollection<ElementId> joined = JoinGeometryUtils.GetJoinedElements(doc, element);
                if (joined.Count > 0) block["joined"] = OutputJson.Ids(joined.Take(50));
            }
            catch
            {
                // Not joinable.
            }
            if (element is Wall wall)
            {
                try
                {
                    var ends = new List<object>();
                    for (int end = 0; end < 2; end++)
                    {
                        ElementArray array = (wall.Location as LocationCurve)?.get_ElementsAtJoin(end);
                        if (array == null) { ends.Add(new List<object>()); continue; }
                        var ids = new List<object>();
                        foreach (Element other in array)
                        {
                            if (other.Id != wall.Id) ids.Add(other.Id.Value);
                        }
                        ends.Add(ids);
                    }
                    block["joins_at_ends"] = ends;
                }
                catch
                {
                    // Unjoined.
                }
            }
            if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId) block["group"] = element.GroupId.Value;
            try
            {
                if (element.AssemblyInstanceId != null && element.AssemblyInstanceId != ElementId.InvalidElementId) block["assembly"] = element.AssemblyInstanceId.Value;
            }
            catch
            {
                // No assembly.
            }
            if (element.OwnerViewId != null && element.OwnerViewId != ElementId.InvalidElementId) block["owner_view"] = ReadKit.NameOf(doc, element.OwnerViewId);
            if (doc.IsWorkshared)
            {
                try { block["workset"] = doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name; } catch { }
                try
                {
                    string owner = WorksharingUtils.GetWorksharingTooltipInfo(doc, element.Id)?.Owner;
                    if (!string.IsNullOrEmpty(owner)) block["owner"] = owner;
                }
                catch
                {
                    // Not available.
                }
            }
            try
            {
                if (element.DesignOption != null) block["design_option"] = element.DesignOption.Name;
            }
            catch
            {
                // Main model.
            }
            string phase = ReadKit.NameOf(doc, ReadKit.BipId(element, BuiltInParameter.PHASE_CREATED));
            if (phase != null) block["phase_created"] = phase;
            string demolished = ReadKit.NameOf(doc, ReadKit.BipId(element, BuiltInParameter.PHASE_DEMOLISHED));
            if (demolished != null) block["phase_demolished"] = demolished;
            return block;
        }

        private static List<object> RoomRef(Room room)
        {
            return new List<object> { room.Id.Value, room.Number, ReadKit.BipString(room, BuiltInParameter.ROOM_NAME) };
        }

        private static Room RoomAt(Element element)
        {
            try
            {
                XYZ point = Geometry.AnchorOf(element);
                if (point == null) return null;
                Document doc = element.Document;
                Phase phase = null;
                ElementId phaseId = ReadKit.BipId(element, BuiltInParameter.PHASE_CREATED);
                if (phaseId != null && phaseId != ElementId.InvalidElementId) phase = doc.GetElement(phaseId) as Phase;
                Room room = phase == null ? doc.GetRoomAtPoint(point) : doc.GetRoomAtPoint(point, phase);
                if (room == null)
                {
                    // Points on the floor slab top: probe slightly above.
                    XYZ above = point + new XYZ(0, 0, 1.0);
                    room = phase == null ? doc.GetRoomAtPoint(above) : doc.GetRoomAtPoint(above, phase);
                }
                return room;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // MEP connectors
        // ---------------------------------------------------------------------------------------------------------

        private static List<object> Connectors(Element element, Transform linkTransform)
        {
            var list = new List<object>();
            ConnectorManager manager = null;
            try
            {
                if (element is MEPCurve curve) manager = curve.ConnectorManager;
                else if (element is FamilyInstance instance) manager = instance.MEPModel?.ConnectorManager;
            }
            catch
            {
                manager = null;
            }
            if (manager == null) return list;
            foreach (Connector connector in manager.Connectors)
            {
                if (connector == null) continue;
                var row = new Dictionary<string, object>(StringComparer.Ordinal);
                try { row["id"] = connector.Id; } catch { }
                try { row["domain"] = connector.Domain.ToString(); } catch { }
                try { row["shape"] = connector.Shape.ToString(); } catch { }
                try
                {
                    XYZ origin = connector.Origin;
                    row["origin"] = OutputJson.Point(linkTransform == null ? origin : linkTransform.OfPoint(origin));
                }
                catch
                {
                    // Non-physical connector.
                }
                try
                {
                    if (connector.Shape == ConnectorProfileType.Round) row["diameter"] = Units.Mm(connector.Radius * 2);
                    else if (connector.Shape == ConnectorProfileType.Rectangular || connector.Shape == ConnectorProfileType.Oval)
                    {
                        row["width"] = Units.Mm(connector.Width);
                        row["height"] = Units.Mm(connector.Height);
                    }
                }
                catch
                {
                    // Size not defined.
                }
                try
                {
                    MEPSystem system = connector.MEPSystem;
                    if (system != null) row["system"] = system.Name;
                }
                catch
                {
                    // No system.
                }
                var connected = new List<object>();
                try
                {
                    if (connector.IsConnected)
                    {
                        foreach (Connector other in connector.AllRefs)
                        {
                            if (other?.Owner == null || other.Owner.Id == element.Id) continue;
                            if (other.ConnectorType == ConnectorType.Logical) continue;
                            connected.Add(other.Owner.Id.Value);
                        }
                    }
                }
                catch
                {
                    // Unconnected.
                }
                row["connected"] = connected;
                list.Add(row);
            }
            return list;
        }
    }
}
