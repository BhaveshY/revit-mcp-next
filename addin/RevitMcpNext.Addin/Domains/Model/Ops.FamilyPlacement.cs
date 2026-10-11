using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// place_family.place (D1 §4B, D3 §3.8): instances of any loadable family by its placement type — level-based,
    /// wall/floor/ceiling/roof hosted (host 'auto' finds the nearest valid host), work-plane/face based, two-level
    /// (columns), line based (start + end) and adaptive (points = placement points). View-based (detail) families are
    /// refused with UNSUPPORTED_OP (annotate places those).
    /// </summary>
    internal static class FamilyPlacementOps
    {
        private const int MaxInstances = 1000;
        /// <summary>Auto host search radius in plan (mm → feet).</summary>
        private static readonly double AutoHostRadius = Units.MmToFt(2000);

        private enum HostKind { None, Wall, Floor, Ceiling, Roof, Face }

        [Op("place_family.place")]
        public static void Place(ChangeContext c)
        {
            Document doc = c.Doc;
            FamilySymbol symbol = Resolve.Type<FamilySymbol>(doc, c.Str("type"), c.Args.Field("type"));
            Family family = symbol.Family ?? throw OpException.InvalidArgs(c.Args.Field("type"), "'" + Resolve.FullTypeName(symbol) + "' has no family");
            FamilyPlacementType placement = family.FamilyPlacementType;
            if (placement == FamilyPlacementType.ViewBased || placement == FamilyPlacementType.CurveBasedDetail)
            {
                throw new OpException(ErrorCodes.UnsupportedOp,
                    "'" + Resolve.FullTypeName(symbol) + "' is a view-based (detail/annotation) family; place_family places model families.",
                    new Dictionary<string, object> { ["alternative"] = "annotate", ["placement"] = placement.ToString() });
            }
            ModelKit.Activate(doc, symbol);

            Level level = c.Level("level", required: false);
            double offset = c.Mm("offset", 0);
            double rotation = c.Deg("rotation", 0);
            bool flipHand = c.Bool("flip_hand");
            bool flipFacing = c.Bool("flip_facing");
            bool allowPinned = c.Bool("allow_pinned");
            string hostText = c.Str("host");
            bool autoHost = string.IsNullOrWhiteSpace(hostText) || hostText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            Element explicitHost = autoHost ? null : WallOps.SingleHost(c, "host");

            var placed = new List<FamilyInstance>();
            var table = new TableBuilder(new[] { "id", "level", "host", "at" });
            var hostsUsed = new HashSet<long>();

            if (placement == FamilyPlacementType.Adaptive || AdaptiveComponentFamilyUtils.IsAdaptiveComponentFamily(family))
            {
                FamilyInstance adaptive = PlaceAdaptive(c, symbol, level);
                Record(c, adaptive, placed);
                table.Add(adaptive.Id.Value, level?.Name, null, ModelKit.Pt(Geometry.AnchorOf(adaptive)));
            }
            else if (c.Args.Has("start") || c.Args.Has("end"))
            {
                if (!(placement == FamilyPlacementType.CurveBased || placement == FamilyPlacementType.CurveDrivenStructural))
                {
                    throw OpException.InvalidArgs(c.Args.Field("start"), "start/end place line-based families; '" + Resolve.FullTypeName(symbol) + "' is " + Describe(placement) + ": use at or points");
                }
                Level lineLevel = level ?? DefaultLevel(c, null);
                XYZ start = ModelKit.Point3(c, "start", lineLevel, offset);
                XYZ end = ModelKit.Point3(c, "end", lineLevel, offset);
                Line line = ModelKit.LineOf(doc, start, end, c.Args.Field("end"));
                FamilyInstance instance = placement == FamilyPlacementType.CurveDrivenStructural
                    ? doc.Create.NewFamilyInstance(line, symbol, lineLevel, ModelKit.StructuralTypeOf(symbol))
                    : PlaceLineOnPlane(c, symbol, lineLevel, line);
                Record(c, instance, placed);
                table.Add(instance.Id.Value, lineLevel?.Name, null, ModelKit.Pt(start) + "-" + ModelKit.Pt(end));
            }
            else
            {
                if (placement == FamilyPlacementType.CurveBased || placement == FamilyPlacementType.CurveDrivenStructural)
                {
                    throw OpException.InvalidArgs(c.Args.Field("start"), "'" + Resolve.FullTypeName(symbol) + "' is line based: give start and end",
                        new Dictionary<string, object> { ["op"] = "place", ["type"] = c.Str("type"), ["start"] = new List<object> { 0, 0 }, ["end"] = new List<object> { 3000, 0 } });
                }
                List<XYZ> raw = c.Args.Has("points")
                    ? c.Args.Points("points", double.NaN)
                    : new List<XYZ> { c.Args.PointOpt("at", double.NaN) ?? throw OpException.InvalidArgs("at", "give at [x,y], points [[x,y],...] or start + end") };
                if (raw.Count == 0) throw OpException.InvalidArgs(c.Args.Field("points"), "needs at least one point");
                if (raw.Count > MaxInstances) throw OpException.InvalidArgs(c.Args.Field("points"), "places at most " + MaxInstances + " instances per call (got " + raw.Count + ")");

                HostKind hostKind = HostKindOf(symbol, placement);
                for (int index = 0; index < raw.Count; index++)
                {
                    PlacedRow row = PlaceOne(c, symbol, placement, hostKind, raw[index], level, offset, rotation, explicitHost, allowPinned, index);
                    Record(c, row.Instance, placed);
                    if (row.Host != null)
                    {
                        hostsUsed.Add(row.Host.Id.Value);
                        c.Modified(row.Host);
                    }
                    if (table.Rows.Count < 50) table.Add(row.Instance.Id.Value, row.Level?.Name, row.Host?.Id.Value, ModelKit.Pt(row.Point));
                }
            }

            ApplyFlips(c, placed, flipHand, flipFacing);
            c.EnsureRegenerated();
            table.Total = placed.Count;
            c.Output("type", symbol.Id);
            c.Output("family", family.Id);
            c.Data(new Dictionary<string, object> { ["type"] = Resolve.FullTypeName(symbol), ["instances"] = table.ToWire() });
            List<string> levelNames = placed.Select(i => ModelKit.LevelOf(doc, i)?.Name).Where(n => n != null).Distinct().Take(3).ToList();
            c.Summary("placed " + ModelKit.Count(placed.Count, "instance") + " of " + Resolve.FullTypeName(symbol) +
                      (levelNames.Count > 0 ? " on " + string.Join(", ", levelNames) : string.Empty) +
                      (hostsUsed.Count > 0 ? " in " + ModelKit.Count(hostsUsed.Count, "host") : string.Empty));
        }

        private sealed class PlacedRow
        {
            public FamilyInstance Instance;
            public Element Host;
            public Level Level;
            public XYZ Point;
        }

        private static void Record(ChangeContext c, FamilyInstance instance, List<FamilyInstance> placed)
        {
            if (instance == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the family instance.");
            c.Created(instance);
            placed.Add(instance);
        }

        /// <summary>One point: hosted (wall/floor/ceiling/roof/face), two-level, work-plane or level-based.</summary>
        private static PlacedRow PlaceOne(ChangeContext c, FamilySymbol symbol, FamilyPlacementType placement, HostKind hostKind, XYZ raw,
            Level level, double offset, double rotation, Element explicitHost, bool allowPinned, int index)
        {
            Document doc = c.Doc;
            string field = c.Args.Has("points") ? c.Args.Field("points") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]" : c.Args.Field("at");
            bool hasZ = !double.IsNaN(raw.Z);

            // Hosted by a wall/floor/ceiling/roof.
            if (placement == FamilyPlacementType.OneLevelBasedHosted)
            {
                Element host = explicitHost ?? FindHost(c, hostKind, raw, level, field);
                CheckHost(c, host, allowPinned);
                Level hostLevel = level ?? ModelKit.LevelOf(doc, host) ?? DefaultLevel(c, null);
                FamilyInstance instance;
                XYZ point;
                if (host is Wall wall)
                {
                    Curve curve = (wall.Location as LocationCurve)?.Curve ?? throw OpException.InvalidArgs(c.Args.Field("host"), "wall " + wall.Id.Value + " has no location line");
                    IntersectionResult hit = curve.Project(new XYZ(raw.X, raw.Y, curve.GetEndPoint(0).Z));
                    if (hit == null) throw OpException.InvalidArgs(field, "point " + ModelKit.Pt(raw) + " cannot be projected onto wall " + wall.Id.Value);
                    point = new XYZ(hit.XYZPoint.X, hit.XYZPoint.Y, ModelKit.Z(hostLevel));
                    instance = doc.Create.NewFamilyInstance(point, symbol, wall, hostLevel, StructuralType.NonStructural);
                    double? sill = hasZ ? raw.Z : (c.Args.Has("offset") ? offset : (double?)null);
                    if (sill.HasValue && instance != null && !ModelKit.TrySet(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, sill.Value))
                    {
                        ModelKit.SetOrWarn(c, instance, BuiltInParameter.INSTANCE_ELEVATION_PARAM, sill.Value, "sill height/offset");
                    }
                    if (Math.Abs(rotation) > 1e-9 && instance != null) c.Warn(WarningCodes.ParamIgnored, "rotation is ignored for wall-hosted instances (use flip_hand/flip_facing)", new[] { instance.Id });
                }
                else
                {
                    point = HostPoint(c, host, raw);
                    instance = doc.Create.NewFamilyInstance(point, symbol, host, hostLevel, StructuralType.NonStructural);
                    if (instance != null) Rotate(c, instance, point, rotation);
                }
                return new PlacedRow { Instance = instance, Host = host, Level = hostLevel, Point = point };
            }

            // Face / work-plane based: on the nearest face of a named host, else on the level plane.
            if (placement == FamilyPlacementType.WorkPlaneBased)
            {
                if (explicitHost != null)
                {
                    CheckHost(c, explicitHost, allowPinned);
                    Level hostLevel = level ?? ModelKit.LevelOf(doc, explicitHost) ?? DefaultLevel(c, null);
                    XYZ target = hasZ
                        ? new XYZ(raw.X, raw.Y, level != null ? ModelKit.Z(level) + raw.Z : raw.Z)
                        : new XYZ(raw.X, raw.Y, ModelKit.Z(hostLevel) + offset);
                    PlanarFace face = NearestFace(explicitHost, target)
                        ?? throw OpException.InvalidArgs(c.Args.Field("host"), ModelKit.Describe(explicitHost) + " has no planar face to host the instance");
                    XYZ onFace = face.Project(target)?.XYZPoint ?? target;
                    FamilyInstance hosted = doc.Create.NewFamilyInstance(face.Reference, onFace, FaceDirection(face, rotation), symbol);
                    return new PlacedRow { Instance = hosted, Host = explicitHost, Level = hostLevel, Point = onFace };
                }
                Level planeLevel = level ?? (hasZ ? Resolve.NearestLevel(doc, raw.Z) : DefaultLevel(c, null));
                double height = hasZ ? (level != null ? raw.Z : raw.Z - ModelKit.Z(planeLevel)) : offset;
                var onLevel = new XYZ(raw.X, raw.Y, ModelKit.Z(planeLevel));
                FamilyInstance free = doc.Create.NewFamilyInstance(planeLevel.GetPlaneReference(), onLevel, new XYZ(Math.Cos(rotation), Math.Sin(rotation), 0), symbol);
                if (free != null && Math.Abs(height) > 1e-9 && !ModelKit.TrySet(free, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, height))
                {
                    ModelKit.SetOrWarn(c, free, BuiltInParameter.INSTANCE_ELEVATION_PARAM, height, "offset");
                }
                return new PlacedRow { Instance = free, Level = planeLevel, Point = new XYZ(raw.X, raw.Y, ModelKit.Z(planeLevel) + height) };
            }

            // Level based (furniture, equipment, fixtures, generic) and two-level based (columns).
            Level baseLevel = level ?? (hasZ ? Resolve.NearestLevel(doc, raw.Z) : DefaultLevel(c, null));
            double z = hasZ ? (level != null ? ModelKit.Z(level) + raw.Z : raw.Z) : ModelKit.Z(baseLevel) + offset;
            var location = new XYZ(raw.X, raw.Y, z);
            StructuralType structuralType = StructuralType.NonStructural;
            if (placement == FamilyPlacementType.TwoLevelsBased && ModelKit.CategoryOf(symbol) == BuiltInCategory.OST_StructuralColumns) structuralType = StructuralType.Column;
            FamilyInstance placedInstance = doc.Create.NewFamilyInstance(location, symbol, baseLevel, structuralType);
            if (placedInstance != null)
            {
                FixElevation(c, placedInstance, baseLevel, z);
                if (placement == FamilyPlacementType.TwoLevelsBased)
                {
                    Level above = Resolve.LevelAbove(doc, baseLevel);
                    if (above != null) ModelKit.TrySet(placedInstance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, above.Id);
                }
                Rotate(c, placedInstance, location, rotation);
            }
            return new PlacedRow { Instance = placedInstance, Level = baseLevel, Point = location };
        }

        /// <summary>A line-based (non-structural) family on the level's plane.</summary>
        private static FamilyInstance PlaceLineOnPlane(ChangeContext c, FamilySymbol symbol, Level level, Line line)
        {
            double z = ModelKit.Z(level);
            Line flat = ModelKit.LineOf(c.Doc, ModelKit.Flat(line.GetEndPoint(0), z), ModelKit.Flat(line.GetEndPoint(1), z), c.Args.Field("end"));
            FamilyInstance instance;
            try
            {
                instance = c.Doc.Create.NewFamilyInstance(level.GetPlaneReference(), flat, symbol);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                instance = c.Doc.Create.NewFamilyInstance(flat, symbol, level, StructuralType.NonStructural);
            }
            double height = line.GetEndPoint(0).Z - z;
            if (instance != null && Math.Abs(height) > 1e-9 && !ModelKit.TrySet(instance, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, height))
            {
                ModelKit.SetOrWarn(c, instance, BuiltInParameter.INSTANCE_ELEVATION_PARAM, height, "offset");
            }
            return instance;
        }

        /// <summary>Adaptive component: points (or at) are the placement points, in order.</summary>
        private static FamilyInstance PlaceAdaptive(ChangeContext c, FamilySymbol symbol, Level level)
        {
            Document doc = c.Doc;
            Level baseLevel = level ?? DefaultLevel(c, null);
            double offset = c.Mm("offset", 0);
            List<XYZ> points = c.Args.Has("points")
                ? ModelKit.Points3(c, "points", baseLevel, offset)
                : new List<XYZ> { ModelKit.Point3(c, "at", baseLevel, offset) };
            FamilyInstance instance = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc, symbol);
            IList<ElementId> placementIds = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(instance);
            if (points.Count != placementIds.Count)
            {
                throw OpException.InvalidArgs(c.Args.Field("points"), "'" + Resolve.FullTypeName(symbol) + "' is adaptive with " + placementIds.Count.ToString(CultureInfo.InvariantCulture) +
                    " placement points; give exactly that many points (got " + points.Count.ToString(CultureInfo.InvariantCulture) + ")");
            }
            for (int i = 0; i < placementIds.Count; i++)
            {
                if (doc.GetElement(placementIds[i]) is ReferencePoint point) point.Position = points[i];
            }
            return instance;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Hosts
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>The host class a hosted family needs: doors/windows → walls, else the family's host setting.</summary>
        private static HostKind HostKindOf(FamilySymbol symbol, FamilyPlacementType placement)
        {
            if (placement == FamilyPlacementType.WorkPlaneBased) return HostKind.Face;
            if (placement != FamilyPlacementType.OneLevelBasedHosted) return HostKind.None;
            BuiltInCategory category = ModelKit.CategoryOf(symbol);
            if (category == BuiltInCategory.OST_Doors || category == BuiltInCategory.OST_Windows) return HostKind.Wall;
            try
            {
                Parameter hosting = symbol.Family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR);
                if (hosting != null && hosting.HasValue && hosting.StorageType == StorageType.Integer)
                {
                    switch (hosting.AsInteger())
                    {
                        case 1: return HostKind.Wall;
                        case 2: return HostKind.Floor;
                        case 3: return HostKind.Ceiling;
                        case 4: return HostKind.Roof;
                    }
                }
            }
            catch
            {
                // Not exposed on this family; fall back to walls.
            }
            return HostKind.Wall;
        }

        /// <summary>host 'auto': the nearest wall (plan distance to its location line) or the floor/ceiling/roof over/under the point.</summary>
        private static Element FindHost(ChangeContext c, HostKind kind, XYZ raw, Level level, string field)
        {
            Document doc = c.Doc;
            var p = new XYZ(raw.X, raw.Y, 0);
            if (kind == HostKind.Wall)
            {
                double z = level != null ? ModelKit.Z(level) : double.NaN;
                Wall best = null;
                double bestDistance = double.MaxValue;
                foreach (Wall wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
                {
                    if (!(wall.Location is LocationCurve location)) continue;
                    if (wall.WallType != null && wall.WallType.Kind == WallKind.Curtain) continue;
                    if (!double.IsNaN(z))
                    {
                        BoundingBoxXYZ box = wall.get_BoundingBox(null);
                        if (box == null || box.Min.Z > z + Units.MmToFt(1) || box.Max.Z < z + Units.MmToFt(1)) continue;
                    }
                    Curve curve = location.Curve;
                    IntersectionResult hit = curve.Project(new XYZ(raw.X, raw.Y, curve.GetEndPoint(0).Z));
                    if (hit == null) continue;
                    double distance = Geometry.DistanceXY(hit.XYZPoint, p);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = wall;
                    }
                }
                if (best != null && bestDistance <= AutoHostRadius) return best;
                throw OpException.InvalidArgs(field, "no wall within 2000 mm of " + ModelKit.Pt(raw) + (level != null ? " on " + level.Name : string.Empty) + "; draw a wall first or pass host");
            }

            Type cls = kind == HostKind.Floor ? typeof(Floor) : kind == HostKind.Ceiling ? typeof(Ceiling) : typeof(RoofBase);
            double reference = level != null ? ModelKit.Z(level) : 0;
            Element found = new FilteredElementCollector(doc).OfClass(cls).WhereElementIsNotElementType().ToElements()
                .Select(e => new { Element = e, Box = e.get_BoundingBox(null) })
                .Where(x => x.Box != null && raw.X >= x.Box.Min.X && raw.X <= x.Box.Max.X && raw.Y >= x.Box.Min.Y && raw.Y <= x.Box.Max.Y)
                .OrderBy(x => Math.Abs((x.Box.Min.Z + x.Box.Max.Z) / 2 - reference))
                .Select(x => x.Element)
                .FirstOrDefault();
            if (found != null) return found;
            string what = kind == HostKind.Floor ? "floor" : kind == HostKind.Ceiling ? "ceiling" : "roof";
            throw OpException.InvalidArgs(field, "no " + what + " contains " + ModelKit.Pt(raw) + "; create one first or pass host");
        }

        private static void CheckHost(ChangeContext c, Element host, bool allowPinned)
        {
            if (host.Pinned && !allowPinned)
            {
                var example = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string key in c.Args.Keys) example[key] = c.Args.Get(key);
                example["allow_pinned"] = true;
                throw OpException.InvalidArgs(c.Args.Field("allow_pinned"), ModelKit.Describe(host) + " is pinned; pass allow_pinned:true to insert into it anyway", example);
            }
            WorksharingGuard.EnsureEditable(c.Doc, host);
        }

        /// <summary>Insertion point on a floor/roof (top) or ceiling (bottom) over/under the plan point.</summary>
        private static XYZ HostPoint(ChangeContext c, Element host, XYZ raw)
        {
            BoundingBoxXYZ box = host.get_BoundingBox(null);
            double z = box == null ? 0 : (host is Ceiling ? box.Min.Z : box.Max.Z);
            var probe = new XYZ(raw.X, raw.Y, z);
            PlanarFace face = NearestFace(host, probe);
            IntersectionResult hit = face?.Project(probe);
            return hit != null ? hit.XYZPoint : probe;
        }

        private static PlanarFace NearestFace(Element host, XYZ point)
        {
            var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            GeometryElement geometry = host.get_Geometry(options);
            if (geometry == null) return null;
            PlanarFace best = null;
            double bestDistance = double.MaxValue;
            foreach (Solid solid in Solids(geometry))
            {
                foreach (Face face in solid.Faces)
                {
                    if (!(face is PlanarFace planar) || planar.Reference == null) continue;
                    IntersectionResult hit = planar.Project(point);
                    if (hit == null) continue;
                    if (hit.Distance < bestDistance)
                    {
                        bestDistance = hit.Distance;
                        best = planar;
                    }
                }
            }
            return best;
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid solid && solid.Faces.Size > 0) yield return solid;
                else if (item is GeometryInstance instance)
                {
                    foreach (Solid inner in Solids(instance.GetInstanceGeometry())) yield return inner;
                }
            }
        }

        /// <summary>A reference direction in the face plane, rotated by <paramref name="rotation"/> about the face normal.</summary>
        private static XYZ FaceDirection(PlanarFace face, double rotation)
        {
            XYZ x = face.XVector.Normalize();
            XYZ y = face.FaceNormal.CrossProduct(x).Normalize();
            return (x * Math.Cos(rotation) + y * Math.Sin(rotation)).Normalize();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Levels and post-placement
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>The level of the active plan of this document, else the level closest to elevation 0.</summary>
        internal static Level DefaultLevel(ChangeContext c, View view)
        {
            View active = view;
            if (active == null)
            {
                try { active = c.Ctx.UiDoc?.ActiveView ?? c.Doc.ActiveView; } catch { }
            }
            if (active is ViewPlan plan && plan.GenLevel != null && plan.Document.Equals(c.Doc)) return plan.GenLevel;
            Level level = new FilteredElementCollector(c.Doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => Math.Abs(l.ProjectElevation)).FirstOrDefault();
            return level ?? throw new OpException(ErrorCodes.NotFound, "The document has no levels; create one with create_elements op level.",
                new Dictionary<string, object> { ["param"] = "level", ["kind"] = "level", ["value"] = null, ["candidates"] = new List<string>() });
        }

        /// <summary>Puts a level-based instance at z (Revit may drop the point z to the level).</summary>
        private static void FixElevation(ChangeContext c, FamilyInstance instance, Level level, double z)
        {
            ModelKit.TryRegenerate(c.Doc);
            if (!(instance.Location is LocationPoint location)) return;
            if (Math.Abs(z - location.Point.Z) < Units.MmToFt(0.5)) return;
            double height = z - ModelKit.Z(level);
            if (ModelKit.TrySet(instance, BuiltInParameter.INSTANCE_ELEVATION_PARAM, height)) return;
            if (ModelKit.TrySet(instance, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, height)) return;
            if (ModelKit.TrySet(instance, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, height)) return;
            c.Warn(WarningCodes.ParamIgnored, "the height above level could not be set on " + ModelKit.Describe(instance), new[] { instance.Id });
        }

        private static void Rotate(ChangeContext c, FamilyInstance instance, XYZ point, double rotation)
        {
            if (Math.Abs(rotation) < 1e-9) return;
            Line axis = Line.CreateBound(point, point + XYZ.BasisZ);
            ElementTransformUtils.RotateElement(c.Doc, instance.Id, axis, rotation);
        }

        private static void ApplyFlips(ChangeContext c, List<FamilyInstance> placed, bool hand, bool facing)
        {
            if (!hand && !facing) return;
            ModelKit.TryRegenerate(c.Doc);
            var skipped = new HashSet<ElementId>();
            foreach (FamilyInstance instance in placed)
            {
                if (hand)
                {
                    if (instance.CanFlipHand) instance.flipHand();
                    else skipped.Add(instance.Id);
                }
                if (facing)
                {
                    if (instance.CanFlipFacing) instance.flipFacing();
                    else skipped.Add(instance.Id);
                }
            }
            if (skipped.Count > 0) c.Warn(WarningCodes.ParamIgnored, "flip_hand/flip_facing is not supported by " + skipped.Count.ToString(CultureInfo.InvariantCulture) + " instance(s)", skipped);
        }

        private static string Describe(FamilyPlacementType placement)
        {
            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased: return "level based";
                case FamilyPlacementType.OneLevelBasedHosted: return "hosted";
                case FamilyPlacementType.TwoLevelsBased: return "two-level based";
                case FamilyPlacementType.WorkPlaneBased: return "work-plane based";
                case FamilyPlacementType.CurveBased: return "line based";
                case FamilyPlacementType.CurveDrivenStructural: return "structural line based";
                case FamilyPlacementType.Adaptive: return "adaptive";
                default: return placement.ToString();
            }
        }
    }
}
