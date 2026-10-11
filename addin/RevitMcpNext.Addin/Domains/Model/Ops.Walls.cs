using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>create_elements wall, opening, shaft, curtain_grid (D1 §4B, D3 §3.8, SPEC §5.4/§5.9).</summary>
    internal static class WallOps
    {
        /// <summary>WALL_KEY_REF_PARAM values: 0 wall centerline ... 5 core face interior.</summary>
        private static readonly string[] LocationLines = { "center", "core_center", "finish_ext", "finish_int", "core_ext", "core_int" };

        [Op("create_elements.wall")]
        public static void Wall(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            WallType type = ModelKit.TypeOrDefault<WallType>(c, "type", BuiltInCategory.OST_Walls, "wall");
            Level top = c.Level("top", required: false);
            double offset = c.Mm("offset", 0);
            double topOffset = c.Mm("top_offset", 0);
            double height = c.Mm("height", 3000);
            bool structural = c.Bool("structural");
            bool flip = c.Bool("flip");
            bool closed = c.Bool("closed");
            string line = c.Args.Enum("line", "center", LocationLines);
            int lineIndex = Array.IndexOf(LocationLines, line);
            double z = ModelKit.Z(level);

            if (top != null)
            {
                if (ModelKit.Z(top) + topOffset <= z + offset + Geometry.MinSegmentFeet)
                {
                    throw OpException.InvalidArgs(c.Args.Field("top"), "top '" + top.Name + "' + top_offset must be above the wall base ('" + level.Name + "' + offset)");
                }
                height = ModelKit.Z(top) + topOffset - z - offset;
            }
            else if (height <= Geometry.MinSegmentFeet)
            {
                throw OpException.InvalidArgs(c.Args.Field("height"), "must be greater than 0 mm");
            }

            List<Curve> curves = BaseCurves(c, z, closed);
            var walls = new List<Wall>();
            foreach (Curve curve in curves)
            {
                Wall wall = Autodesk.Revit.DB.Wall.Create(doc, curve, type.Id, level.Id, height, offset, flip, structural);
                c.Created(wall);
                walls.Add(wall);
                if (top != null)
                {
                    ModelKit.TrySet(wall, BuiltInParameter.WALL_HEIGHT_TYPE, top.Id);
                    ModelKit.TrySet(wall, BuiltInParameter.WALL_TOP_OFFSET, topOffset);
                }
                if (lineIndex > 0)
                {
                    ModelKit.TrySet(wall, BuiltInParameter.WALL_KEY_REF_PARAM, lineIndex);
                }
            }

            if (lineIndex > 0)
            {
                // Changing the location line keeps the wall in place and moves its location curve; put the chosen
                // line back on the requested path, as drawing with that location line would.
                ModelKit.TryRegenerate(doc);
                for (int index = 0; index < walls.Count; index++)
                {
                    if (!(walls[index].Location is LocationCurve location)) continue;
                    Curve wanted = curves[index];
                    if (location.Curve.GetEndPoint(0).DistanceTo(wanted.GetEndPoint(0)) > Units.MmToFt(0.1) ||
                        location.Curve.GetEndPoint(1).DistanceTo(wanted.GetEndPoint(1)) > Units.MmToFt(0.1))
                    {
                        location.Curve = wanted;
                    }
                }
            }
            c.EnsureRegenerated();

            ModelKit.Outputs(c, type, level);
            c.Data(new Dictionary<string, object>
            {
                ["type"] = Resolve.FullTypeName(type),
                ["level"] = level.Name,
                ["top"] = top?.Name,
                ["height"] = top == null ? Units.Mm(height) : null,
                ["line"] = lineIndex > 0 ? line : null
            });
            string vertical = top != null
                ? ", top " + top.Name + (Math.Abs(topOffset) > 1e-9 ? " " + (topOffset > 0 ? "+" : string.Empty) + ModelKit.Mm(topOffset) + " mm" : string.Empty)
                : ", height " + ModelKit.Mm(height) + " mm";
            c.Summary("created " + ModelKit.Count(walls.Count, "wall") + " (" + Resolve.FullTypeName(type) + ") on " + level.Name + vertical +
                      (curves.Any(cv => cv is Arc) ? ", arc" : string.Empty));
        }

        /// <summary>Wall base curves at z: start/end (+through for an arc) or one line per segment of points.</summary>
        private static List<Curve> BaseCurves(ChangeContext c, double z, bool closed)
        {
            Document doc = c.Doc;
            List<XYZ> points;
            if (c.Args.Has("points")) points = ModelKit.FlatPoints(c, "points", z);
            else if (c.Args.Has("start") && c.Args.Has("end")) points = new List<XYZ> { ModelKit.Flat(c.Point("start"), z), ModelKit.Flat(c.Point("end"), z) };
            else throw OpException.InvalidArgs("points", "give points [[x,y],...] or start + end");

            if (c.Args.Has("through"))
            {
                if (points.Count != 2) throw OpException.InvalidArgs(c.Args.Field("through"), "makes an arc from exactly 2 points (start and end)");
                return new List<Curve> { ModelKit.ArcOf(points[0], points[1], ModelKit.Flat(c.Point("through"), z), c.Args.Field("through")) };
            }
            return ModelKit.Polyline(doc, points, closed, c.Args.Has("points") ? c.Args.Field("points") : c.Args.Field("end"));
        }

        // -------------------------------------------------------------------------------------------------------------
        // Openings and shafts
        // -------------------------------------------------------------------------------------------------------------

        [Op("create_elements.opening")]
        public static void Opening(ChangeContext c)
        {
            Document doc = c.Doc;
            Element host = SingleHost(c, "host");
            WorksharingGuard.EnsureEditable(doc, host);

            Opening opening;
            string where;
            if (host is Wall wall)
            {
                if (!(wall.Location is LocationCurve location)) throw OpException.InvalidArgs(c.Args.Field("host"), "wall " + wall.Id.Value + " has no location line");
                Curve curve = location.Curve;
                List<XYZ> marks = c.Args.Has("start") && c.Args.Has("end")
                    ? new List<XYZ> { c.Point("start"), c.Point("end") }
                    : c.Args.Points("points");
                if (marks.Count < 2) throw OpException.InvalidArgs(c.Args.Field("start"), "a wall opening needs start and end (model points on the wall)");
                double sill = c.Mm("offset", 0);
                double height = c.Mm("height", 2100);
                if (height <= Geometry.MinSegmentFeet) throw OpException.InvalidArgs(c.Args.Field("height"), "must be greater than 0 mm");

                double curveZ = curve.GetEndPoint(0).Z;
                var projected = new List<(double param, XYZ point)>();
                foreach (XYZ mark in marks)
                {
                    IntersectionResult hit = curve.Project(new XYZ(mark.X, mark.Y, curveZ));
                    if (hit == null) throw OpException.InvalidArgs(c.Args.Field("start"), "point " + ModelKit.Pt(mark) + " cannot be projected onto wall " + wall.Id.Value);
                    if (Geometry.DistanceXY(hit.XYZPoint, mark) > Units.MmToFt(1000))
                    {
                        c.Warn(WarningCodes.ValueClamped, "point " + ModelKit.Pt(mark) + " is " + ModelKit.Mm(Geometry.DistanceXY(hit.XYZPoint, mark)) + " mm from the wall line; it was projected onto it", new[] { wall.Id });
                    }
                    projected.Add((hit.Parameter, hit.XYZPoint));
                }
                projected.Sort((a, b) => a.param.CompareTo(b.param));
                XYZ a0 = projected[0].point, a1 = projected[projected.Count - 1].point;
                if (Geometry.DistanceXY(a0, a1) < Geometry.MinSegmentFeet) throw OpException.InvalidArgs(c.Args.Field("end"), "start and end project onto the same point of the wall");

                double baseZ = BaseElevation(doc, wall);
                opening = doc.Create.NewOpening(wall, new XYZ(a0.X, a0.Y, baseZ + sill), new XYZ(a1.X, a1.Y, baseZ + sill + height));
                where = "wall " + wall.Id.Value + ", " + ModelKit.Mm(Geometry.DistanceXY(a0, a1)) + " x " + ModelKit.Mm(height) + " mm, sill " + ModelKit.Mm(sill) + " mm";
            }
            else if (host is Floor || host is RoofBase || host is Ceiling)
            {
                Level level = ModelKit.LevelOf(doc, host);
                List<XYZ> points = ModelKit.FlatPoints(c, "points", ModelKit.Z(level));
                CurveArray profile = ModelKit.ToArray(ModelKit.Loop(doc, points, c.Args.Field("points")));
                opening = doc.Create.NewOpening(host, profile, true);
                where = ModelKit.Describe(host) + ", " + Units.Number(Units.M2(Geometry.PolygonAreaXY(points)), 2).ToString() + " m2";
            }
            else
            {
                throw OpException.InvalidArgs(c.Args.Field("host"), ModelKit.Describe(host) + " cannot host an opening; use a wall, floor, roof or ceiling id (or op shaft for a vertical opening between levels)");
            }

            if (opening == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the opening in " + ModelKit.Describe(host) + ".");
            c.Created(opening);
            c.Modified(host);
            c.Output("opening", opening.Id);
            c.Summary("created opening in " + where);
        }

        [Op("create_elements.shaft")]
        public static void Shaft(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            Level top = c.Level("top");
            double offset = c.Mm("offset", 0);
            double topOffset = c.Mm("top_offset", 0);
            if (ModelKit.Z(top) + topOffset <= ModelKit.Z(level) + offset + Geometry.MinSegmentFeet)
            {
                throw OpException.InvalidArgs(c.Args.Field("top"), "top '" + top.Name + "' + top_offset must be above '" + level.Name + "' + offset");
            }
            List<XYZ> points = ModelKit.FlatPoints(c, "points", ModelKit.Z(level));
            CurveArray profile = ModelKit.ToArray(ModelKit.Loop(doc, points, c.Args.Field("points")));
            Level bottomLevel = level, topLevel = top;
            if (ModelKit.Z(top) <= ModelKit.Z(level))
            {
                // Same (or reversed) levels with offsets: NewOpening needs top above bottom; use the next level up and fix the offsets.
                topLevel = Resolve.LevelAbove(doc, level) ?? throw OpException.InvalidArgs(c.Args.Field("top"), "must be a level above '" + level.Name + "'");
                topOffset = ModelKit.Z(top) + topOffset - ModelKit.Z(topLevel);
            }
            Opening shaft = doc.Create.NewOpening(bottomLevel, topLevel, profile);
            if (shaft == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the shaft opening.");
            c.Created(shaft);
            if (Math.Abs(offset) > 1e-9) ModelKit.SetOrWarn(c, shaft, BuiltInParameter.WALL_BASE_OFFSET, offset, "base offset");
            if (Math.Abs(topOffset) > 1e-9) ModelKit.SetOrWarn(c, shaft, BuiltInParameter.WALL_TOP_OFFSET, topOffset, "top offset");
            ModelKit.Outputs(c, null, level);
            c.Summary("created shaft opening from " + level.Name + (Math.Abs(offset) > 1e-9 ? " " + ModelKit.Mm(offset) + " mm" : string.Empty) + " to " + top.Name +
                      ", " + Units.Number(Units.M2(Geometry.PolygonAreaXY(points)), 2).ToString() + " m2");
        }

        /// <summary>The single element of a host-like param (INVALID_ARGS when several are given).</summary>
        internal static Element SingleHost(ChangeContext c, string key)
        {
            IList<Element> hosts = c.Elements(key);
            if (hosts.Count != 1) throw OpException.InvalidArgs(c.Args.Field(key), "must name exactly one element (got " + hosts.Count.ToString(CultureInfo.InvariantCulture) + ")");
            return hosts[0];
        }

        /// <summary>Elevation of a wall's base (level + base offset).</summary>
        internal static double BaseElevation(Document doc, Wall wall)
        {
            Level level = doc.GetElement(wall.LevelId) as Level ?? ModelKit.LevelOf(doc, wall);
            return ModelKit.Z(level) + (ModelKit.GetDouble(wall, BuiltInParameter.WALL_BASE_OFFSET) ?? 0);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Curtain grids (V9: which AddGridLine flag gives vertical lines is checked on every call and learned)
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>Learned AddGridLine flag for vertical lines on walls (null until one line was added).</summary>
        private static bool? _uFlagForVertical;

        [Op("create_elements.curtain_grid")]
        public static void CurtainGrid(ChangeContext c)
        {
            Document doc = c.Doc;
            Element host = SingleHost(c, "host");
            if (!(host is Wall wall) || wall.CurtainGrid == null)
            {
                throw OpException.InvalidArgs(c.Args.Field("host"), ModelKit.Describe(host) + " is not a curtain wall (curtain grids need a curtain wall host)");
            }
            WorksharingGuard.EnsureEditable(doc, wall);
            string direction = c.Args.Enum("direction", null, "vertical", "horizontal") ?? throw OpException.InvalidArgs(c.Args.Field("direction"), "is required (vertical or horizontal)");
            bool vertical = direction == "vertical";
            MullionType mullion = c.Args.Has("mullion") ? Resolve.Type<MullionType>(doc, c.Str("mullion"), c.Args.Field("mullion")) : null;

            Curve curve = (wall.Location as LocationCurve)?.Curve ?? throw OpException.InvalidArgs(c.Args.Field("host"), "wall " + wall.Id.Value + " has no location line");
            BoundingBoxXYZ box = Geometry.BoxOf(wall) ?? throw OpException.InvalidArgs(c.Args.Field("host"), "wall " + wall.Id.Value + " has no geometry");
            double baseZ = BaseElevation(doc, wall);
            double height = box.Max.Z - baseZ;
            double length = curve.Length;
            double extent = vertical ? length : height;

            List<double> positions = ModelKit.LengthsFt(c, "positions");
            if (positions.Count == 0)
            {
                double spacing = c.Args.MmOpt("spacing") ?? throw OpException.InvalidArgs(c.Args.Field("positions"), "give positions [mm,...] or spacing mm");
                if (spacing <= Geometry.MinSegmentFeet) throw OpException.InvalidArgs(c.Args.Field("spacing"), "must be greater than 0 mm");
                for (double s = spacing; s < extent - Units.MmToFt(1); s += spacing) positions.Add(s);
                if (positions.Count > 500) throw OpException.InvalidArgs(c.Args.Field("spacing"), "would add " + positions.Count + " grid lines (at most 500)");
            }

            CurtainGrid grid = wall.CurtainGrid;
            var lines = new List<ElementId>();
            var mullions = new List<ElementId>();
            double midZ = baseZ + height / 2.0;
            foreach (double s in positions)
            {
                if (s <= Units.MmToFt(1) || s >= extent - Units.MmToFt(1))
                {
                    c.Warn(WarningCodes.ValueClamped, "position " + ModelKit.Mm(s) + " mm is outside the wall (0-" + ModelKit.Mm(extent) + " mm) and was skipped", new[] { wall.Id });
                    continue;
                }
                XYZ point = vertical
                    ? Flat(curve.Evaluate(s / length, true), midZ)
                    : Flat(curve.Evaluate(0.5, true), baseZ + s);
                CurtainGridLine created = AddLine(c, grid, vertical, point, s);
                if (created == null) continue;
                c.Created(created);
                lines.Add(created.Id);
                if (mullion != null)
                {
                    Curve segment = created.AllSegmentCurves.Cast<Curve>().FirstOrDefault();
                    if (segment != null)
                    {
                        foreach (Mullion m in created.AddMullions(segment, mullion, false).Cast<Mullion>()) { c.Created(m); mullions.Add(m.Id); }
                    }
                }
            }
            if (lines.Count == 0) throw OpException.InvalidArgs(c.Args.Field("positions"), "no grid line was added (all positions are outside the wall)");
            c.Modified(wall);
            c.Summary("added " + ModelKit.Count(lines.Count, direction + " grid line") + " to curtain wall " + wall.Id.Value +
                      (mullion != null ? " with " + ModelKit.Count(mullions.Count, "mullion") + " (" + Resolve.FullTypeName(mullion) + ")" : string.Empty));
        }

        private static XYZ Flat(XYZ p, double z) => new XYZ(p.X, p.Y, z);

        /// <summary>Adds one grid line; verifies its orientation and retries with the other U/V flag when it is wrong (V9).</summary>
        private static CurtainGridLine AddLine(ChangeContext c, CurtainGrid grid, bool vertical, XYZ point, double s)
        {
            bool flag = _uFlagForVertical.HasValue ? (vertical ? _uFlagForVertical.Value : !_uFlagForVertical.Value) : !vertical;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                CurtainGridLine line;
                try
                {
                    line = grid.AddGridLine(flag, point, false);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    if (attempt == 0 && !_uFlagForVertical.HasValue) { flag = !flag; continue; }
                    c.Warn(WarningCodes.ValueClamped, "no grid line at " + ModelKit.Mm(s) + " mm: " + ex.Message);
                    return null;
                }
                if (line == null) return null;
                XYZ d = (line.FullCurve.GetEndPoint(1) - line.FullCurve.GetEndPoint(0)).Normalize();
                bool isVertical = Math.Abs(d.Z) > 0.9;
                if (isVertical == vertical)
                {
                    _uFlagForVertical = vertical ? flag : !flag;
                    return line;
                }
                // Wrong orientation: remove it and use the other flag.
                c.Doc.Delete(line.Id);
                flag = !flag;
            }
            c.Warn(WarningCodes.ValueClamped, "no " + (vertical ? "vertical" : "horizontal") + " grid line could be added at " + ModelKit.Mm(s) + " mm");
            return null;
        }
    }
}
