using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// annotate dimension | dimension_edit | spot (D3 §3.5). Linear dimensions take stable reference strings (refs) or
    /// elements (ids, references picked by mode) and a dimension line start→end on the view plane. dimension_edit changes
    /// type, text overrides and position in place; new references or a new line recreate the dimension (Revit cannot
    /// rebind references). Spots pick the host face under 'at'. Both dimension ops are strict (warnings roll back).
    /// </summary>
    internal static class DimensionOps
    {
        [Op("annotate.dimension")]
        public static void Dimension(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "a dimension", allowSheet: false, allow3D: true);
            Plane plane = AnnotationSupport.ViewPlane(view);
            Line line = DimensionLine(c, plane, c.Point("start"), c.Point("end"));
            XYZ direction = line.Direction;

            string mode = c.Args.Enum("mode", "centers", "centers", "faces", "exterior", "core");
            ReferenceArray references = c.Has("refs") ? ParseRefs(c, "refs") : FromElements(c, view, direction, mode);
            if (references.Size < 2)
            {
                throw OpException.InvalidArgs(c.Has("refs") ? "refs" : "ids", "a dimension needs at least 2 references; got " + references.Size.ToString(CultureInfo.InvariantCulture),
                    new Dictionary<string, object> { ["op"] = "dimension", ["view"] = view.Name, ["ids"] = new List<object> { "<grid 1>", "<grid 2>" }, ["start"] = new List<object> { 0, -2000 }, ["end"] = new List<object> { 12000, -2000 } });
            }

            DimensionType type = c.Has("type") ? LinearType(c) : null;
            Dimension dimension = Create(doc, view, line, references, type);
            ApplyOverrides(c, dimension);
            c.Created(dimension);
            c.Output("dimension", dimension.Id);
            c.Data(Describe(doc, dimension, view));
            c.Summary("dimension with " + AnnotationSupport.Count(Math.Max(1, dimension.NumberOfSegments), "segment") + " in '" + view.Name + "'" + TotalText(dimension));
        }

        [Op("annotate.dimension_edit")]
        public static void Edit(ChangeContext c)
        {
            Document doc = c.Doc;
            IList<Element> elements = c.Elements("ids");
            bool rebuild = c.Has("refs") || c.Has("start") || c.Has("end");
            if (c.Has("start") != c.Has("end")) throw OpException.InvalidArgs(c.Has("start") ? "end" : "start", "start and end go together (the new dimension line)");
            if (!rebuild && !c.Has("type") && !c.Has("by") && !c.Has("overrides"))
            {
                throw OpException.InvalidArgs("type", "give at least one of type, by, overrides, refs, start+end",
                    new Dictionary<string, object> { ["op"] = "dimension_edit", ["ids"] = elements.Select(e => (object)e.Id.Value).Take(3).ToList(), ["by"] = new List<object> { 0, 500 } });
            }
            DimensionType type = c.Has("type") ? LinearType(c) : null;
            XYZ by = c.Has("by") ? c.Args.Vector("by") : null;

            var table = new TableBuilder(new[] { "id", "now", "action" });
            foreach (Element element in elements)
            {
                if (!(element is Dimension dimension) || element is SpotDimension)
                {
                    throw OpException.InvalidArgs("ids", "element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + " is not a linear dimension (" + (Rows.CategoryLabel(element) ?? element.GetType().Name) + ")");
                }
                View view = doc.GetElement(dimension.OwnerViewId) as View
                            ?? throw OpException.InvalidArgs("ids", "dimension " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + " has no owner view");
                var actions = new List<string>();
                Dimension target = dimension;
                if (rebuild)
                {
                    Plane plane = AnnotationSupport.ViewPlane(view);
                    Line line = c.Has("start") ? DimensionLine(c, plane, c.Point("start"), c.Point("end")) : ExistingLine(dimension);
                    ReferenceArray references = c.Has("refs") ? ParseRefs(c, "refs") : dimension.References;
                    if (references.Size < 2) throw OpException.InvalidArgs("refs", "a dimension needs at least 2 references; got " + references.Size.ToString(CultureInfo.InvariantCulture));
                    DimensionType keep = type ?? doc.GetElement(dimension.GetTypeId()) as DimensionType;
                    List<TextOverride> texts = ReadOverrides(dimension);
                    target = Create(doc, view, line, references, keep);
                    if (!c.Has("refs") || target.NumberOfSegments == dimension.NumberOfSegments) WriteOverrides(target, texts);
                    c.Created(target);
                    c.Delete(new List<ElementId> { dimension.Id });
                    actions.Add(c.Has("refs") ? "references replaced (recreated)" : "line moved (recreated)");
                }
                else if (type != null && dimension.GetTypeId() != type.Id)
                {
                    string before = doc.GetElement(dimension.GetTypeId())?.Name;
                    dimension.ChangeTypeId(type.Id);
                    c.Modified(dimension, "type", before);
                    actions.Add("type " + type.Name);
                }
                if (by != null)
                {
                    XYZ normal = AnnotationSupport.ViewPlane(view).Normal;
                    XYZ move = by - by.DotProduct(normal) * normal;
                    if (!move.IsZeroLength())
                    {
                        ElementTransformUtils.MoveElement(doc, target.Id, move);
                        if (target.Id == dimension.Id) c.Modified(dimension, "position", null);
                        actions.Add("moved");
                    }
                }
                if (c.Has("overrides"))
                {
                    ApplyOverrides(c, target);
                    if (target.Id == dimension.Id) c.Modified(dimension, "text", null);
                    actions.Add("text overrides");
                }
                table.Add(dimension.Id.Value, target.Id.Value, actions.Count == 0 ? "unchanged" : string.Join(", ", actions));
                if (table.Rows.Count == 1) c.Output("dimension", target.Id);
            }
            c.Data(new Dictionary<string, object> { ["dimensions"] = table.ToWire() });
            c.Summary("edited " + AnnotationSupport.Count(elements.Count, "dimension") + (rebuild ? " (recreated with the new references/line)" : string.Empty));
        }

        [Op("annotate.spot")]
        public static void Spot(ChangeContext c)
        {
            Document doc = c.Doc;
            string kind = c.Args.Enum("kind", null, "elevation", "coordinate", "slope");
            if (kind == null) throw OpException.InvalidArgs("kind", "is required (elevation, coordinate or slope)");
            if (kind == "slope")
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "The Revit API cannot create spot slopes (Revit " + c.Ctx.Year.ToString(CultureInfo.InvariantCulture) + ").",
                    new Dictionary<string, object> { ["kind"] = "slope", ["alternative"] = "place spot slopes in Revit, or use kind elevation at two points" });
            }
            View view = AnnotationSupport.AnnotationView(c, "a spot dimension", allowSheet: false, allow3D: true);
            string hostText = c.Str("host");
            if (string.IsNullOrWhiteSpace(hostText)) throw OpException.InvalidArgs("host", "is required (the element whose face carries the spot)");
            Element host = Resolve.ById<Element>(doc, hostText);
            if (host == null)
            {
                try { host = doc.GetElement(hostText.Trim()); } catch (Autodesk.Revit.Exceptions.ApplicationException) { host = null; }
            }
            if (host == null) throw OpException.NotFound("host", "element", hostText);

            XYZ at = c.Point("at");
            Tuple<Reference, XYZ> face = DimensionReferences.SpotFace(host, view, at);
            if (face == null)
            {
                throw OpException.InvalidArgs("at", "no face of element " + host.Id.Value.ToString(CultureInfo.InvariantCulture) + " (" + (Rows.CategoryLabel(host) ?? host.GetType().Name) +
                                                    ") lies under " + Describe(at) + " in '" + view.Name + "'; pick a point inside the element as seen in this view");
            }

            XYZ point = face.Item2;
            bool leader = c.Bool("leader", true);
            double paper = Units.MmToFt(8) * Math.Max(1, view.Scale);
            XYZ right = view is ViewPlan ? XYZ.BasisX : view.RightDirection;
            XYZ up = view is ViewPlan ? XYZ.BasisY : view.UpDirection;
            XYZ bend = point + (right + up) * paper;
            XYZ end = bend + right * paper;
            SpotDimension spot = kind == "elevation"
                ? doc.Create.NewSpotElevation(view, face.Item1, point, bend, end, point, leader)
                : doc.Create.NewSpotCoordinate(view, face.Item1, point, bend, end, point, leader);
            if (spot == null) throw c.Error(ErrorCodes.RevitRefused, "Revit did not create the spot " + kind + " on element " + host.Id.Value.ToString(CultureInfo.InvariantCulture) + ".");
            if (c.Has("type"))
            {
                SpotDimensionType type = Resolve.Type<SpotDimensionType>(doc, c.Str("type"), c.Args.Field("type"));
                if (spot.GetTypeId() != type.Id) spot.ChangeTypeId(type.Id);
            }
            c.Created(spot);
            c.Output("spot", spot.Id);
            c.Data(new Dictionary<string, object>
            {
                ["id"] = spot.Id.Value,
                ["kind"] = kind,
                ["host"] = host.Id.Value,
                ["point"] = OutputJson.Point(point),
                ["type"] = doc.GetElement(spot.GetTypeId())?.Name
            });
            c.Summary("spot " + kind + " on element " + host.Id.Value.ToString(CultureInfo.InvariantCulture) + " in '" + view.Name + "'");
        }

        // ---------------------------------------------------------------------------------------------------------

        private static Line DimensionLine(ChangeContext c, Plane plane, XYZ start, XYZ end)
        {
            XYZ a = AnnotationSupport.OnPlane(plane, start);
            XYZ b = AnnotationSupport.OnPlane(plane, end);
            if (a.DistanceTo(b) < Units.MmToFt(1))
            {
                throw OpException.InvalidArgs("end", "the dimension line needs start and end at least 1 mm apart on the view plane");
            }
            return Line.CreateBound(a, b);
        }

        private static Line ExistingLine(Dimension dimension)
        {
            Line line = dimension.Curve as Line;
            if (line == null) throw OpException.InvalidArgs("ids", "dimension " + dimension.Id.Value.ToString(CultureInfo.InvariantCulture) + " is not linear; give start and end");
            if (line.IsBound) return line;
            XYZ origin = dimension.Origin ?? line.Origin;
            return Line.CreateBound(origin - line.Direction * 5, origin + line.Direction * 5);
        }

        private static ReferenceArray ParseRefs(ChangeContext c, string key)
        {
            var array = new ReferenceArray();
            List<string> values = c.Args.Strs(key);
            for (int index = 0; index < values.Count; index++)
            {
                Reference reference;
                try
                {
                    reference = Reference.ParseFromStableRepresentation(c.Doc, values[index]);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    reference = null;
                }
                if (reference == null)
                {
                    throw OpException.InvalidArgs(c.Args.Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                        "'" + values[index] + "' is not a stable reference of this document (take refs from describe_elements)");
                }
                array.Append(reference);
            }
            return array;
        }

        private static ReferenceArray FromElements(ChangeContext c, View view, XYZ direction, string mode)
        {
            if (!c.Has("ids")) throw OpException.InvalidArgs("ids", "give refs (stable references) or ids (elements to dimension)");
            var array = new ReferenceArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Element element in c.Elements("ids"))
            {
                foreach (Reference reference in DimensionReferences.For(element, view, direction, mode, c))
                {
                    string key;
                    try { key = reference.ConvertToStableRepresentation(c.Doc); } catch (Autodesk.Revit.Exceptions.ApplicationException) { key = Guid.NewGuid().ToString("N"); }
                    if (seen.Add(key)) array.Append(reference);
                }
            }
            return array;
        }

        private static DimensionType LinearType(ChangeContext c)
        {
            DimensionType type = Resolve.Type<DimensionType>(c.Doc, c.Str("type"), c.Args.Field("type"));
            if (type is SpotDimensionType || (type.StyleType != DimensionStyleType.Linear && type.StyleType != DimensionStyleType.LinearFixed))
            {
                throw OpException.InvalidArgs("type", "'" + type.Name + "' is a " + type.StyleType + " dimension type; give a linear dimension type");
            }
            return type;
        }

        private static Dimension Create(Document doc, View view, Line line, ReferenceArray references, DimensionType type)
        {
            Dimension dimension = type == null ? doc.Create.NewDimension(view, line, references) : doc.Create.NewDimension(view, line, references, type);
            if (dimension == null)
            {
                throw new OpException(ErrorCodes.RevitRefused,
                    "Revit could not dimension these references in '" + view.Name + "' (they must be parallel to each other and cross the dimension line).",
                    new Dictionary<string, object> { ["apiMessage"] = "NewDimension returned null" });
            }
            return dimension;
        }

        private sealed class TextOverride
        {
            public int Segment;
            public string Value, Prefix, Suffix, Above, Below;
        }

        private static List<TextOverride> ReadOverrides(Dimension dimension)
        {
            var list = new List<TextOverride>();
            if (dimension.NumberOfSegments == 0)
            {
                list.Add(new TextOverride { Segment = 0, Value = dimension.ValueOverride, Prefix = dimension.Prefix, Suffix = dimension.Suffix, Above = dimension.Above, Below = dimension.Below });
                return list;
            }
            int index = 0;
            foreach (DimensionSegment segment in dimension.Segments)
            {
                list.Add(new TextOverride { Segment = index++, Value = segment.ValueOverride, Prefix = segment.Prefix, Suffix = segment.Suffix, Above = segment.Above, Below = segment.Below });
            }
            return list;
        }

        private static void WriteOverrides(Dimension dimension, List<TextOverride> texts)
        {
            foreach (TextOverride text in texts)
            {
                if (dimension.NumberOfSegments == 0)
                {
                    if (text.Segment != 0) continue;
                    SetText(text, v => dimension.ValueOverride = v, v => dimension.Prefix = v, v => dimension.Suffix = v, v => dimension.Above = v, v => dimension.Below = v);
                    continue;
                }
                if (text.Segment >= dimension.NumberOfSegments) continue;
                DimensionSegment segment = dimension.Segments.get_Item(text.Segment);
                SetText(text, v => segment.ValueOverride = v, v => segment.Prefix = v, v => segment.Suffix = v, v => segment.Above = v, v => segment.Below = v);
            }
        }

        private static void SetText(TextOverride text, Action<string> value, Action<string> prefix, Action<string> suffix, Action<string> above, Action<string> below)
        {
            if (!string.IsNullOrEmpty(text.Value)) value(text.Value);
            if (!string.IsNullOrEmpty(text.Prefix)) prefix(text.Prefix);
            if (!string.IsNullOrEmpty(text.Suffix)) suffix(text.Suffix);
            if (!string.IsNullOrEmpty(text.Above)) above(text.Above);
            if (!string.IsNullOrEmpty(text.Below)) below(text.Below);
        }

        /// <summary>overrides: [{segment (1-based; omit = all segments), value, prefix, suffix, above, below}]; "" clears a text.</summary>
        private static void ApplyOverrides(ChangeContext c, Dimension dimension)
        {
            if (!c.Has("overrides")) return;
            List<PayloadReader> items = c.Args.SubList("overrides");
            foreach (PayloadReader item in items)
            {
                int? segment = item.IntOpt("segment");
                var targets = new List<Action<PayloadReader>>();
                int count = dimension.NumberOfSegments;
                if (count == 0)
                {
                    if (segment.HasValue && segment.Value != 1)
                    {
                        throw OpException.InvalidArgs(item.Field("segment"), "this dimension has one segment; use 1 or omit segment");
                    }
                    targets.Add(o => Apply(o, v => dimension.ValueOverride = v, v => dimension.Prefix = v, v => dimension.Suffix = v, v => dimension.Above = v, v => dimension.Below = v));
                }
                else if (!segment.HasValue)
                {
                    foreach (DimensionSegment each in dimension.Segments)
                    {
                        DimensionSegment s = each;
                        targets.Add(o => Apply(o, v => s.ValueOverride = v, v => s.Prefix = v, v => s.Suffix = v, v => s.Above = v, v => s.Below = v));
                    }
                }
                else
                {
                    if (segment.Value < 1 || segment.Value > count)
                    {
                        throw OpException.InvalidArgs(item.Field("segment"), "must be 1-" + count.ToString(CultureInfo.InvariantCulture) + " (segments of this dimension, left to right as created)");
                    }
                    DimensionSegment s = dimension.Segments.get_Item(segment.Value - 1);
                    targets.Add(o => Apply(o, v => s.ValueOverride = v, v => s.Prefix = v, v => s.Suffix = v, v => s.Above = v, v => s.Below = v));
                }
                foreach (Action<PayloadReader> target in targets) target(item);
            }
        }

        private static void Apply(PayloadReader item, Action<string> value, Action<string> prefix, Action<string> suffix, Action<string> above, Action<string> below)
        {
            if (item.TryGet("value", out object v)) value(Text(v));
            if (item.TryGet("prefix", out object p)) prefix(Text(p));
            if (item.TryGet("suffix", out object s)) suffix(Text(s));
            if (item.TryGet("above", out object a)) above(Text(a));
            if (item.TryGet("below", out object b)) below(Text(b));
        }

        private static string Text(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object> Describe(Document doc, Dimension dimension, View view)
        {
            var values = new List<object>();
            if (dimension.NumberOfSegments == 0)
            {
                values.Add(ValueMm(dimension.Value));
            }
            else
            {
                foreach (DimensionSegment segment in dimension.Segments) values.Add(ValueMm(segment.Value));
            }
            return new Dictionary<string, object>
            {
                ["id"] = dimension.Id.Value,
                ["view"] = view.Name,
                ["type"] = doc.GetElement(dimension.GetTypeId())?.Name,
                ["segments"] = Math.Max(1, dimension.NumberOfSegments),
                ["values"] = values
            };
        }

        private static object ValueMm(double? feet)
        {
            return feet.HasValue ? Units.Mm(feet.Value) : null;
        }

        private static string TotalText(Dimension dimension)
        {
            double total = 0;
            if (dimension.NumberOfSegments == 0) total = dimension.Value ?? 0;
            else foreach (DimensionSegment segment in dimension.Segments) total += segment.Value ?? 0;
            return total > 0 ? ", total " + Units.FtToMm(total).ToString("0.#", CultureInfo.InvariantCulture) + " mm" : string.Empty;
        }

        private static string Describe(XYZ point)
        {
            return "[" + Units.FtToMm(point.X).ToString("0", CultureInfo.InvariantCulture) + ", " + Units.FtToMm(point.Y).ToString("0", CultureInfo.InvariantCulture) + ", " +
                   Units.FtToMm(point.Z).ToString("0", CultureInfo.InvariantCulture) + "] mm";
        }
    }
}
