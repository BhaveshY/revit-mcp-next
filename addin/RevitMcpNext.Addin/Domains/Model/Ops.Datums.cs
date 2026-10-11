using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>create_elements level, grid, ref_plane, model_line (D1 §4B, D3 §3.8).</summary>
    internal static class DatumOps
    {
        [Op("create_elements.level")]
        public static void Level(ChangeContext c)
        {
            Document doc = c.Doc;
            double elevation = c.Mm("elevation");
            int count = c.Args.IntClamped("count", 1, 1, 100, (field, text) => c.Warn(WarningCodes.ValueClamped, field + " " + text));
            double? spacing = c.Args.MmOpt("spacing");
            if (count > 1 && (!spacing.HasValue || Math.Abs(spacing.Value) < Geometry.MinSegmentFeet))
            {
                throw OpException.InvalidArgs(c.Args.Field("spacing"), "is required (non-zero mm) when count > 1");
            }
            bool plans = c.Bool("plans", true);
            string name = c.Str("name");

            List<Level> existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var taken = new HashSet<string>(existing.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(name) && taken.Contains(name.Trim()))
            {
                throw new OpException(ErrorCodes.NameTaken, "A level named '" + name.Trim() + "' already exists.",
                    new Dictionary<string, object> { ["param"] = c.Args.Field("name"), ["kind"] = "level", ["value"] = name.Trim() });
            }
            double tolerance = Units.MmToFt(0.5);
            ViewFamilyType floorType = ViewTypeOf(doc, ViewFamily.FloorPlan);
            ViewFamilyType ceilingType = ViewTypeOf(doc, ViewFamily.CeilingPlan);

            var table = new TableBuilder(new[] { "id", "name", "elevation", "plans" });
            string previousName = null;
            for (int index = 0; index < count; index++)
            {
                double z = elevation + index * (spacing ?? 0);
                Level clash = existing.FirstOrDefault(l => Math.Abs(l.ProjectElevation - z) < tolerance);
                if (clash != null)
                {
                    c.Warn(WarningCodes.RevitWarning, "level '" + clash.Name + "' already exists at " + ModelKit.Mm(z) + " mm; another level was created at the same elevation", new[] { clash.Id });
                }

                Level level = Autodesk.Revit.DB.Level.Create(doc, z);
                c.Created(level);
                string wanted;
                if (index == 0 && !string.IsNullOrWhiteSpace(name)) wanted = name.Trim();
                else if (previousName != null) wanted = ModelKit.NextName(previousName, taken);
                else wanted = AutoLevelName(existing, z, taken);
                if (!taken.Contains(wanted) && !string.Equals(level.Name, wanted, StringComparison.Ordinal)) level.Name = wanted;
                taken.Add(level.Name);
                previousName = level.Name;
                existing.Add(level);

                var made = new List<string>();
                if (plans)
                {
                    made.AddRange(CreatePlan(c, floorType, level, "floor plan"));
                    made.AddRange(CreatePlan(c, ceilingType, level, "ceiling plan"));
                }
                table.Add(level.Id.Value, level.Name, Units.Mm(level.ProjectElevation), string.Join(", ", made));
                if (index == 0) c.Output("level", level.Id);
            }

            c.Data(new Dictionary<string, object> { ["levels"] = table.ToWire() });
            string first = (string)table.Rows[0][1];
            c.Summary(count == 1
                ? "created level '" + first + "' at " + ModelKit.Mm(elevation) + " mm" + (plans ? " with floor and ceiling plans" : string.Empty)
                : "created " + count.ToString(CultureInfo.InvariantCulture) + " levels from '" + first + "' at " + ModelKit.Mm(elevation) + " mm every " + ModelKit.Mm(spacing ?? 0) + " mm" + (plans ? " with plans" : string.Empty));
        }

        private static List<string> CreatePlan(ChangeContext c, ViewFamilyType type, Level level, string what)
        {
            var made = new List<string>();
            if (type == null)
            {
                c.Warn(WarningCodes.PartialResult, "no " + what + " view type in this document; no " + what + " was created for '" + level.Name + "'");
                return made;
            }
            try
            {
                ViewPlan plan = ViewPlan.Create(c.Doc, type.Id, level.Id);
                if (plan != null)
                {
                    c.Created(plan);
                    made.Add(what);
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                c.Warn(WarningCodes.PartialResult, what + " for '" + level.Name + "' was not created: " + ex.Message);
            }
            return made;
        }

        private static ViewFamilyType ViewTypeOf(Document doc, ViewFamily family)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == family);
        }

        /// <summary>Revit-style default: increment the name of the highest level below z ("Level 3" → "Level 4").</summary>
        private static string AutoLevelName(List<Level> levels, double z, ISet<string> taken)
        {
            Level below = levels.Where(l => l.ProjectElevation <= z + 1e-6).OrderBy(l => l.ProjectElevation).LastOrDefault()
                          ?? levels.OrderBy(l => l.ProjectElevation).LastOrDefault();
            return ModelKit.NextName(below?.Name ?? "Level 0", taken);
        }

        [Op("create_elements.grid")]
        public static void Grid(ChangeContext c)
        {
            Document doc = c.Doc;
            XYZ start = ModelKit.Flat(c.Point("start"), 0);
            XYZ end = ModelKit.Flat(c.Point("end"), 0);
            XYZ through = c.Args.Has("through") ? ModelKit.Flat(c.Point("through"), 0) : null;
            int count = c.Args.IntClamped("count", 1, 1, 100, (field, text) => c.Warn(WarningCodes.ValueClamped, field + " " + text));
            double? spacing = c.Args.MmOpt("spacing");
            if (count > 1 && (!spacing.HasValue || Math.Abs(spacing.Value) < Geometry.MinSegmentFeet))
            {
                throw OpException.InvalidArgs(c.Args.Field("spacing"), "is required (non-zero mm) when count > 1");
            }
            string name = c.Str("name");
            HashSet<string> taken = ModelKit.NamesOf<Grid>(doc, g => g.Name);
            if (!string.IsNullOrWhiteSpace(name) && taken.Contains(name.Trim()))
            {
                throw new OpException(ErrorCodes.NameTaken, "A grid named '" + name.Trim() + "' already exists.",
                    new Dictionary<string, object> { ["param"] = c.Args.Field("name"), ["kind"] = "grid", ["value"] = name.Trim() });
            }

            Curve baseCurve = through != null
                ? (Curve)ModelKit.ArcOf(start, end, through, c.Args.Field("through"))
                : ModelKit.LineOf(doc, start, end, c.Args.Field("end"));
            XYZ left = ModelKit.LeftOf(ModelKit.DirXY(start, end, c.Args.Field("end")));

            var table = new TableBuilder(new[] { "id", "name" });
            string previous = null;
            for (int index = 0; index < count; index++)
            {
                double distance = index * (spacing ?? 0);
                Grid grid;
                if (baseCurve is Arc arc)
                {
                    double radius = arc.Radius + distance;
                    if (radius <= Geometry.MinSegmentFeet) throw OpException.InvalidArgs(c.Args.Field("spacing"), "makes the arc radius zero or negative");
                    Arc offset = index == 0 ? arc : Arc.Create(arc.Center, radius, arc.GetEndParameter(0), arc.GetEndParameter(1), arc.XDirection, arc.YDirection);
                    grid = Autodesk.Revit.DB.Grid.Create(doc, offset);
                }
                else
                {
                    XYZ shift = left * distance;
                    grid = Autodesk.Revit.DB.Grid.Create(doc, Line.CreateBound(start + shift, end + shift));
                }
                c.Created(grid);
                string wanted = index == 0 ? name?.Trim() : (previous != null ? ModelKit.NextName(previous, taken) : null);
                if (!string.IsNullOrWhiteSpace(wanted) && !taken.Contains(wanted) && !string.Equals(grid.Name, wanted, StringComparison.Ordinal)) grid.Name = wanted;
                taken.Add(grid.Name);
                previous = grid.Name;
                table.Add(grid.Id.Value, grid.Name);
                if (index == 0) c.Output("grid", grid.Id);
            }

            c.Data(new Dictionary<string, object> { ["grids"] = table.ToWire() });
            string names = string.Join(", ", table.Rows.Select(r => (string)r[1]).Take(12));
            c.Summary("created " + ModelKit.Count(count, "grid") + " (" + names + ")" + (through != null ? " as arcs" : string.Empty));
        }

        [Op("create_elements.ref_plane")]
        public static void RefPlane(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = c.Args.Has("view") ? c.View("view") : DefaultSketchView(c);
            XYZ cut;
            double z = 0;
            if (view is ViewPlan plan)
            {
                cut = XYZ.BasisZ;
                z = ModelKit.Z(plan.GenLevel);
            }
            else if (view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation || view.ViewType == ViewType.Detail)
            {
                cut = view.ViewDirection;
            }
            else
            {
                throw OpException.InvalidArgs(c.Args.Field("view"), "must be a plan, section or elevation view ('" + view.Name + "' is " + view.ViewType + ")");
            }

            XYZ start = c.Args.PointOpt("start", double.NaN) ?? throw OpException.InvalidArgs(c.Args.Field("start"), "is required (a point [x,y] in mm)");
            XYZ end = c.Args.PointOpt("end", double.NaN) ?? throw OpException.InvalidArgs(c.Args.Field("end"), "is required (a point [x,y] in mm)");
            start = new XYZ(start.X, start.Y, double.IsNaN(start.Z) ? z : start.Z);
            end = new XYZ(end.X, end.Y, double.IsNaN(end.Z) ? z : end.Z);
            if (start.DistanceTo(end) <= Math.Max(doc.Application.ShortCurveTolerance, 1e-6)) throw OpException.InvalidArgs(c.Args.Field("end"), "must differ from start");
            if (cut.CrossProduct((end - start).Normalize()).GetLength() < 1e-6) throw OpException.InvalidArgs(c.Args.Field("end"), "start -> end must not be parallel to the view direction");

            ReferencePlane refPlane = doc.Create.NewReferencePlane(start, end, cut, view);
            c.Created(refPlane);
            string name = c.Str("name");
            if (!string.IsNullOrWhiteSpace(name)) ModelKit.Rename(refPlane, name, ModelKit.NamesOf<ReferencePlane>(doc, p => p.Name), "reference plane", c.Args.Field("name"));
            c.Output("ref_plane", refPlane.Id);
            c.Summary("created reference plane" + (string.IsNullOrWhiteSpace(refPlane.Name) ? string.Empty : " '" + refPlane.Name + "'") + " in '" + view.Name + "' from " + ModelKit.Pt(start) + " to " + ModelKit.Pt(end));
        }

        /// <summary>The active plan view, else the first floor plan (reference planes and separators need a view).</summary>
        internal static View DefaultSketchView(ChangeContext c)
        {
            View active = null;
            try { active = c.Ctx.UiDoc?.ActiveView ?? c.Doc.ActiveView; } catch { }
            if (active is ViewPlan plan && !plan.IsTemplate) return plan;
            return ModelKit.PlanOf(c.Doc, null) ?? throw c.Error(ErrorCodes.NotFound, "The document has no plan view; pass view.",
                new Dictionary<string, object> { ["param"] = c.Args.Field("view"), ["kind"] = "view", ["value"] = null, ["candidates"] = new List<string>() });
        }

        [Op("create_elements.model_line")]
        public static void ModelLine(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level", required: false);
            List<XYZ> points = ModelKit.Points3(c, "points", level);
            bool closed = c.Bool("closed");
            GraphicsStyle style = c.Args.Has("line_style") ? Resolve.LineStyle(doc, c.Str("line_style"), c.Args.Field("line_style")) : null;

            List<Curve> curves;
            if (c.Args.Has("through"))
            {
                if (points.Count != 2) throw OpException.InvalidArgs(c.Args.Field("through"), "needs exactly 2 points (start and end of the arc)");
                curves = new List<Curve> { ModelKit.ArcOf(points[0], points[1], ModelKit.Point3(c, "through", level), c.Args.Field("through")) };
            }
            else
            {
                curves = ModelKit.Polyline(doc, points, closed, c.Args.Field("points"));
            }

            // One sketch plane per distinct horizontal plane; sloped segments get a vertical plane through them.
            var planes = new Dictionary<long, SketchPlane>();
            foreach (Curve curve in curves)
            {
                XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
                bool flat = Math.Abs(a.Z - b.Z) < 1e-9 && !(curve is Arc arc && Math.Abs(Math.Abs(arc.Normal.Z) - 1) > 1e-6);
                SketchPlane plane;
                if (flat)
                {
                    long key = (long)Math.Round(a.Z * 1e6);
                    if (!planes.TryGetValue(key, out plane))
                    {
                        plane = ModelKit.HorizontalPlane(doc, level, a.Z);
                        planes[key] = plane;
                    }
                }
                else
                {
                    plane = ModelKit.PlaneFor(doc, curve);
                }
                ModelCurve line = doc.Create.NewModelCurve(curve, plane);
                if (style != null) line.LineStyle = style;
                c.Created(line);
            }
            c.Summary("created " + ModelKit.Count(curves.Count, "model line") + (level != null ? " on " + level.Name : string.Empty) + (style != null ? " (" + style.Name + ")" : string.Empty));
            ModelKit.Outputs(c, null, level);
        }
    }
}
