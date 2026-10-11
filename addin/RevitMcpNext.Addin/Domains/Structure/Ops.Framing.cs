using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMcpNext.Addin
{
    /// <summary>structure column, beam, brace, beam_system (D1 §4B, D3 §3.8).</summary>
    internal static class FramingOps
    {
        private const int MaxInstances = 2000;
        private static readonly double SamePoint = Units.MmToFt(10);

        // -------------------------------------------------------------------------------------------------------------
        // Columns
        // -------------------------------------------------------------------------------------------------------------

        [Op("structure.column")]
        public static void Column(ChangeContext c)
        {
            Document doc = c.Doc;
            FamilySymbol symbol = ColumnSymbol(c);
            ModelKit.Activate(doc, symbol);
            Level level = c.Level("level");
            Level top = c.Level("top", required: false) ?? Resolve.LevelAbove(doc, level);
            if (top != null && ModelKit.Z(top) <= ModelKit.Z(level)) throw OpException.InvalidArgs(c.Args.Field("top"), "top '" + top.Name + "' must be above '" + level.Name + "'");
            double offset = c.Mm("offset", 0);
            double topOffset = c.Mm("top_offset", 0);
            double rotation = c.Deg("rotation", 0);
            StructuralType structuralType = ModelKit.CategoryOf(symbol) == BuiltInCategory.OST_StructuralColumns ? StructuralType.Column : StructuralType.NonStructural;
            double z = ModelKit.Z(level);
            var created = new List<FamilyInstance>();

            if (c.Args.Has("start") && c.Args.Has("end") && !c.Args.Has("at") && !c.Args.Has("points") && !c.Args.Has("grids"))
            {
                XYZ start = ModelKit.Point3(c, "start", level, offset);
                XYZ end = ModelKit.Point3(c, "end", level, offset);
                Line axis = ModelKit.LineOf(doc, start, end, c.Args.Field("end"));
                FamilyInstance slanted = doc.Create.NewFamilyInstance(axis, symbol, level, structuralType);
                if (slanted == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the slanted column.");
                c.Created(slanted);
                created.Add(slanted);
            }
            else
            {
                List<XYZ> points = ColumnPoints(c, z, out string source);
                if (points.Count > MaxInstances) throw OpException.InvalidArgs(c.Args.Field("points"), "places at most " + MaxInstances + " columns per call (got " + points.Count + ")");
                List<XYZ> existing = ExistingColumnPoints(doc, level);
                int skipped = 0;
                foreach (XYZ point in points)
                {
                    if (existing.Any(e => Geometry.DistanceXY(e, point) < SamePoint))
                    {
                        skipped++;
                        continue;
                    }
                    FamilyInstance column = doc.Create.NewFamilyInstance(new XYZ(point.X, point.Y, z), symbol, level, structuralType);
                    if (column == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create a column at " + ModelKit.Pt(point) + ".");
                    c.Created(column);
                    created.Add(column);
                    existing.Add(point);
                    if (top != null) ModelKit.TrySet(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, top.Id);
                    if (Math.Abs(offset) > 1e-9) ModelKit.SetOrWarn(c, column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, offset, "base offset");
                    if (Math.Abs(topOffset) > 1e-9) ModelKit.SetOrWarn(c, column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, topOffset, "top offset");
                    if (Math.Abs(rotation) > 1e-9)
                    {
                        XYZ p = new XYZ(point.X, point.Y, z);
                        ElementTransformUtils.RotateElement(doc, column.Id, Line.CreateBound(p, p + XYZ.BasisZ), rotation);
                    }
                }
                if (skipped > 0) c.Warn(WarningCodes.PartialResult, skipped.ToString(CultureInfo.InvariantCulture) + " point(s) " + source + " already have a column on " + level.Name + " and were skipped");
                if (created.Count == 0) throw OpException.InvalidArgs(c.Args.Field("at"), "every point already has a column on " + level.Name + "; nothing was created");
                if (top == null) c.Warn(WarningCodes.ParamIgnored, "no level above " + level.Name + "; the columns keep Revit's default top");
            }
            c.EnsureRegenerated();
            c.Output("type", symbol.Id);
            c.Output("level", level.Id);
            c.Data(new Dictionary<string, object> { ["type"] = Resolve.FullTypeName(symbol), ["level"] = level.Name, ["top"] = top?.Name, ["count"] = created.Count });
            c.Summary("created " + ModelKit.Count(created.Count, "column") + " (" + Resolve.FullTypeName(symbol) + ") on " + level.Name + (top != null ? " to " + top.Name : string.Empty));
        }

        private static FamilySymbol ColumnSymbol(ChangeContext c)
        {
            try
            {
                return Resolve.Type<FamilySymbol>(c.Doc, c.Str("type"), c.Args.Field("type"), BuiltInCategory.OST_StructuralColumns);
            }
            catch (OpException structural) when (structural.Code == ErrorCodes.NotFound)
            {
                try
                {
                    return Resolve.Type<FamilySymbol>(c.Doc, c.Str("type"), c.Args.Field("type"), BuiltInCategory.OST_Columns);
                }
                catch (OpException)
                {
                    throw structural;
                }
            }
        }

        /// <summary>at, points or grid intersections (grids ['*'] = all) in plan.</summary>
        private static List<XYZ> ColumnPoints(ChangeContext c, double z, out string source)
        {
            if (c.Args.Has("grids"))
            {
                source = "at grid intersections";
                return GridIntersections(c);
            }
            if (c.Args.Has("points"))
            {
                source = "in points";
                return c.Args.Points("points", 0).Select(p => new XYZ(p.X, p.Y, 0)).ToList();
            }
            source = "at the point";
            XYZ at = c.Args.PointOpt("at") ?? throw OpException.InvalidArgs("at", "give at [x,y], points, grids or start + end");
            return new List<XYZ> { new XYZ(at.X, at.Y, 0) };
        }

        private static List<XYZ> GridIntersections(ChangeContext c)
        {
            Document doc = c.Doc;
            List<string> names = c.Args.Strs("grids").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            List<Grid> all = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            List<Grid> grids = names.Any(n => n == "*")
                ? all
                : names.Select(n => Resolve.ById<Grid>(doc, n) ?? Resolve.Single(all, g => g.Name, n, c.Args.Field("grids"), "grid")).Distinct().ToList();
            if (grids.Count < 2) throw OpException.InvalidArgs(c.Args.Field("grids"), "needs at least 2 grids (the document has " + all.Count + ")");

            var result = new List<XYZ>();
            List<Curve> curves = grids.Select(g => Flat(g.Curve)).ToList();
            for (int i = 0; i < curves.Count; i++)
            {
                for (int j = i + 1; j < curves.Count; j++)
                {
                    foreach (XYZ hit in Crossings(curves[i], curves[j]))
                    {
                        XYZ p = new XYZ(hit.X, hit.Y, 0);
                        if (!result.Any(r => r.DistanceTo(p) < SamePoint)) result.Add(p);
                    }
                }
            }
            if (result.Count == 0) throw OpException.InvalidArgs(c.Args.Field("grids"), "the grids " + MepKit.Joined(grids.Select(g => g.Name)) + " do not intersect");
            return result.OrderBy(p => Math.Round(p.Y, 3)).ThenBy(p => p.X).ToList();
        }

        /// <summary>Points where two curves cross (Revit 2027 replaced Intersect(curve, out array) with CurveIntersectResult).</summary>
        private static List<XYZ> Crossings(Curve a, Curve b)
        {
            var points = new List<XYZ>();
#if REVIT2027
            CurveIntersectResult result = a.Intersect(b, CurveIntersectResultOption.Detailed);
            if (result == null || result.Result != SetComparisonResult.Overlap) return points;
            foreach (CurveOverlapPoint overlap in result.GetOverlaps())
            {
                if (overlap.Type == CurveOverlapPointType.Intersection) points.Add(overlap.Point);
            }
#else
            if (a.Intersect(b, out IntersectionResultArray hits) != SetComparisonResult.Overlap || hits == null) return points;
            foreach (IntersectionResult hit in hits) points.Add(hit.XYZPoint);
#endif
            return points;
        }

        private static Curve Flat(Curve curve)
        {
            double z = curve.GetEndPoint(0).Z;
            return Math.Abs(z) < 1e-9 ? curve : curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, -z)));
        }

        private static List<XYZ> ExistingColumnPoints(Document doc, Level level)
        {
            var result = new List<XYZ>();
            foreach (BuiltInCategory category in new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns })
            {
                foreach (FamilyInstance column in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    ElementId baseLevel = column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? column.LevelId;
                    if (baseLevel != level.Id) continue;
                    if (column.Location is LocationPoint point) result.Add(new XYZ(point.Point.X, point.Point.Y, 0));
                }
            }
            return result;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Beams and braces
        // -------------------------------------------------------------------------------------------------------------

        [Op("structure.beam")]
        public static void Beam(ChangeContext c)
        {
            Document doc = c.Doc;
            FamilySymbol symbol = Resolve.Type<FamilySymbol>(doc, c.Str("type"), c.Args.Field("type"), BuiltInCategory.OST_StructuralFraming);
            ModelKit.Activate(doc, symbol);
            Level level = c.Level("level");
            double offset = c.Mm("offset", 0);
            List<XYZ> chain;
            if (c.Args.Has("between")) chain = ColumnChain(c, level, offset);
            else if (c.Args.Has("points")) chain = ModelKit.Points3(c, "points", level, offset);
            else if (c.Args.Has("start") && c.Args.Has("end")) chain = new List<XYZ> { ModelKit.Point3(c, "start", level, offset), ModelKit.Point3(c, "end", level, offset) };
            else throw OpException.InvalidArgs("start", "give start + end, points (a chain) or between (column ids)");
            if (chain.Count < 2) throw OpException.InvalidArgs(c.Args.Field("points"), "needs at least 2 points");
            if (chain.Count - 1 > MaxInstances) throw OpException.InvalidArgs(c.Args.Field("points"), "places at most " + MaxInstances + " beams per call");

            var beams = new List<FamilyInstance>();
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                Line line = ModelKit.LineOf(doc, chain[i], chain[i + 1], c.Args.Field(c.Args.Has("points") ? "points" : "end"));
                FamilyInstance beam = doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.Beam);
                if (beam == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create beam " + i.ToString(CultureInfo.InvariantCulture) + ".");
                c.Created(beam);
                beams.Add(beam);
            }
            c.EnsureRegenerated();
            c.Output("type", symbol.Id);
            c.Output("level", level.Id);
            double length = 0;
            for (int i = 0; i + 1 < chain.Count; i++) length += chain[i].DistanceTo(chain[i + 1]);
            c.Data(new Dictionary<string, object> { ["type"] = Resolve.FullTypeName(symbol), ["level"] = level.Name, ["length"] = Units.Mm(length) });
            c.Summary("created " + ModelKit.Count(beams.Count, "beam") + " (" + Resolve.FullTypeName(symbol) + ") on " + level.Name + ", " + ModelKit.Mm(length) + " mm" +
                      (Math.Abs(offset) > 1e-9 ? ", offset " + ModelKit.Mm(offset) + " mm" : string.Empty));
        }

        /// <summary>between: the plan positions of the named columns, at level + offset.</summary>
        private static List<XYZ> ColumnChain(ChangeContext c, Level level, double offset)
        {
            Document doc = c.Doc;
            var result = new List<XYZ>();
            double z = ModelKit.Z(level) + offset;
            foreach (Element element in c.Elements("between"))
            {
                XYZ p = null;
                if (element.Location is LocationPoint point) p = point.Point;
                else if (element.Location is LocationCurve curve) p = curve.Curve.GetEndPoint(1);
                else p = Geometry.AnchorOf(element);
                if (p == null) throw OpException.InvalidArgs(c.Args.Field("between"), ModelKit.Describe(element) + " has no location");
                BuiltInCategory category = ModelKit.CategoryOf(element);
                if (category != BuiltInCategory.OST_StructuralColumns && category != BuiltInCategory.OST_Columns)
                {
                    c.Warn(WarningCodes.ParamIgnored, ModelKit.Describe(element) + " is not a column; its location is used anyway", new[] { element.Id });
                }
                result.Add(new XYZ(p.X, p.Y, z));
            }
            if (result.Count < 2) throw OpException.InvalidArgs(c.Args.Field("between"), "needs at least 2 columns");
            return result;
        }

        [Op("structure.brace")]
        public static void Brace(ChangeContext c)
        {
            Document doc = c.Doc;
            FamilySymbol symbol = Resolve.Type<FamilySymbol>(doc, c.Str("type"), c.Args.Field("type"), BuiltInCategory.OST_StructuralFraming);
            ModelKit.Activate(doc, symbol);
            Level level = c.Level("level");
            XYZ start = ModelKit.Point3(c, "start", level);
            XYZ end = ModelKit.Point3(c, "end", level);
            Line line = ModelKit.LineOf(doc, start, end, c.Args.Field("end"));
            FamilyInstance brace = doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.Brace);
            if (brace == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the brace.");
            c.Created(brace);
            c.EnsureRegenerated();
            c.Output("type", symbol.Id);
            c.Output("level", level.Id);
            c.Summary("created brace (" + Resolve.FullTypeName(symbol) + ") from " + ModelKit.Pt(start) + " z " + ModelKit.Mm(start.Z - ModelKit.Z(level)) +
                      " to " + ModelKit.Pt(end) + " z " + ModelKit.Mm(end.Z - ModelKit.Z(level)) + " mm above " + level.Name);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Beam systems
        // -------------------------------------------------------------------------------------------------------------

        [Op("structure.beam_system")]
        public static void BeamSystemOp(ChangeContext c)
        {
            Document doc = c.Doc;
            FamilySymbol symbol = Resolve.Type<FamilySymbol>(doc, c.Str("type"), c.Args.Field("type"), BuiltInCategory.OST_StructuralFraming);
            ModelKit.Activate(doc, symbol);
            Level level = c.Level("level");
            double z = ModelKit.Z(level);
            CurveLoop loop = FloorRoomOps.OrientedLoop(doc, ModelKit.FlatPoints(c, "points", z), c.Args.Field("points"), counterClockwise: true);
            IList<Curve> profile = loop.ToList();
            double angle = c.Deg("direction", 0);
            var direction = new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
            SketchPlane plane = ModelKit.HorizontalPlane(doc, level, z);

            BeamSystem system = BeamSystem.Create(doc, profile, plane, direction, false);
            if (system == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the beam system.");
            c.Created(system);
            system.BeamType = symbol;
            double? spacing = c.Args.MmOpt("spacing");
            if (spacing.HasValue)
            {
                if (spacing.Value <= Units.MmToFt(50)) throw OpException.InvalidArgs(c.Args.Field("spacing"), "must be at least 50 mm");
                system.LayoutRule = new LayoutRuleFixedDistance(spacing.Value, BeamSystemJustifyType.Center);
            }
            c.EnsureRegenerated();
            ICollection<ElementId> beams = system.GetBeamIds();
            c.Created(beams);
            c.Output("type", symbol.Id);
            c.Output("level", level.Id);
            c.Output("beam_system", system.Id);
            c.Data(new Dictionary<string, object> { ["beam_system"] = system.Id.Value, ["beams"] = beams.Count, ["direction_deg"] = Units.Angle(angle), ["spacing"] = spacing.HasValue ? Units.Mm(spacing.Value) : null });
            c.Summary("created beam system on " + level.Name + " with " + ModelKit.Count(beams.Count, "beam") + " (" + Resolve.FullTypeName(symbol) + ")" +
                      (spacing.HasValue ? " every " + ModelKit.Mm(spacing.Value) + " mm" : string.Empty) + ", direction " + Units.RadToDeg(angle).ToString("0.#", CultureInfo.InvariantCulture) + " deg");
        }
    }
}
