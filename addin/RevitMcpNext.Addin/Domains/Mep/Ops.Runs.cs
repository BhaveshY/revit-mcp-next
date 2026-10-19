using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// mep pipe, duct, conduit, cable_tray, flex_pipe, flex_duct (D1 §4B, D3 §3.8): one segment per pair of points,
    /// elbows at bends when fittings (default true). A fitting Revit refuses is rolled back alone and reported as
    /// warn FITTING_FAILED; the segments stay. z of a point is height above level, else level + offset (default 2700).
    /// </summary>
    internal static class MepRunOps
    {
        private const double DefaultOffsetMm = 2700;
        private const int MaxSegments = 500;

        [Op("mep.pipe")]
        public static void PipeRun(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            PipeType type = ModelKit.TypeOrDefault<PipeType>(c, "type", BuiltInCategory.OST_PipeCurves, "pipe");
            PipingSystemType system = MepKit.SystemType<PipingSystemType>(c, "system", t => t.SystemClassification == MEPSystemClassification.DomesticColdWater, "piping");
            double? size = c.Args.MmOpt("size");
            List<XYZ> points = PathWithSlope(c, level);
            RunResult run = BuildRun(c, points,
                (a, b) => Pipe.Create(doc, system.Id, type.Id, level.Id, a, b),
                curve => { if (size.HasValue) MepKit.SetSize(c, curve, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, size.Value, "diameter"); });
            Finish(c, run, "pipe", Resolve.FullTypeName(type), system.Name, level, size.HasValue ? "DN " + ModelKit.Mm(size.Value) : null);
            c.Output("system_type", system.Id);
        }

        [Op("mep.duct")]
        public static void DuctRun(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            double? size = c.Args.MmOpt("size");
            double? width = c.Args.MmOpt("width");
            double? height = c.Args.MmOpt("height");
            if (size.HasValue && (width.HasValue || height.HasValue)) throw OpException.InvalidArgs(c.Args.Field("size"), "give size (round) or width + height (rectangular), not both");
            if (width.HasValue != height.HasValue) throw OpException.InvalidArgs(c.Args.Field(width.HasValue ? "height" : "width"), "width and height go together");
            DuctType type = DuctTypeFor(c, round: size.HasValue ? true : (width.HasValue ? false : (bool?)null));
            MechanicalSystemType system = MepKit.SystemType<MechanicalSystemType>(c, "system", t => t.SystemClassification == MEPSystemClassification.SupplyAir, "duct");
            List<XYZ> points = ModelKit.Points3(c, "points", level, c.Mm("offset", DefaultOffsetMm));
            RunResult run = BuildRun(c, points,
                (a, b) => Duct.Create(doc, system.Id, type.Id, level.Id, a, b),
                curve =>
                {
                    if (size.HasValue) MepKit.SetSize(c, curve, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, size.Value, "diameter");
                    if (width.HasValue) MepKit.SetSize(c, curve, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, width.Value, "width");
                    if (height.HasValue) MepKit.SetSize(c, curve, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, height.Value, "height");
                });
            string sizeText = size.HasValue ? "dia " + ModelKit.Mm(size.Value) : width.HasValue ? ModelKit.Mm(width.Value) + " x " + ModelKit.Mm(height.Value) : null;
            Finish(c, run, "duct", Resolve.FullTypeName(type), system.Name, level, sizeText);
            c.Output("system_type", system.Id);
        }

        [Op("mep.conduit")]
        public static void ConduitRun(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            ConduitType type = ModelKit.TypeOrDefault<ConduitType>(c, "type", BuiltInCategory.OST_Conduit, "conduit");
            double? size = c.Args.MmOpt("size");
            List<XYZ> points = ModelKit.Points3(c, "points", level, c.Mm("offset", DefaultOffsetMm));
            RunResult run = BuildRun(c, points,
                (a, b) => Conduit.Create(doc, type.Id, a, b, level.Id),
                curve => { if (size.HasValue) MepKit.SetSize(c, curve, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, size.Value, "diameter"); });
            Finish(c, run, "conduit", Resolve.FullTypeName(type), null, level, size.HasValue ? "dia " + ModelKit.Mm(size.Value) : null);
        }

        [Op("mep.cable_tray")]
        public static void CableTrayRun(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            CableTrayType type = ModelKit.TypeOrDefault<CableTrayType>(c, "type", BuiltInCategory.OST_CableTray, "cable tray");
            double? width = c.Args.MmOpt("width");
            double? height = c.Args.MmOpt("height");
            List<XYZ> points = ModelKit.Points3(c, "points", level, c.Mm("offset", DefaultOffsetMm));
            RunResult run = BuildRun(c, points,
                (a, b) => CableTray.Create(doc, type.Id, a, b, level.Id),
                curve =>
                {
                    if (width.HasValue) MepKit.SetSize(c, curve, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, width.Value, "width");
                    if (height.HasValue) MepKit.SetSize(c, curve, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, height.Value, "height");
                });
            string sizeText = width.HasValue || height.HasValue ? (width.HasValue ? ModelKit.Mm(width.Value) : "?") + " x " + (height.HasValue ? ModelKit.Mm(height.Value) : "?") : null;
            Finish(c, run, "cable tray", Resolve.FullTypeName(type), null, level, sizeText);
        }

        [Op("mep.flex_pipe")]
        public static void FlexPipeRun(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            FlexPipeType type = ModelKit.TypeOrDefault<FlexPipeType>(c, "type", BuiltInCategory.OST_FlexPipeCurves, "flex pipe");
            PipingSystemType system = MepKit.SystemType<PipingSystemType>(c, "system", t => t.SystemClassification == MEPSystemClassification.DomesticColdWater, "piping");
            List<XYZ> points = FlexPoints(c, level);
            FlexPipe flex = FlexPipe.Create(doc, system.Id, type.Id, level.Id, points);
            if (flex == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the flex pipe.");
            c.Created(flex);
            double? size = c.Args.MmOpt("size");
            if (size.HasValue) MepKit.SetSize(c, flex, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, size.Value, "diameter");
            c.EnsureRegenerated();
            c.Output("type", type.Id);
            c.Output("level", level.Id);
            c.Summary("created flex pipe (" + Resolve.FullTypeName(type) + ", " + system.Name + ") on " + level.Name + " through " + ModelKit.Count(points.Count, "point") +
                      (size.HasValue ? ", dia " + ModelKit.Mm(size.Value) + " mm" : string.Empty));
        }

        [Op("mep.flex_duct")]
        public static void FlexDuctRun(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            FlexDuctType type = ModelKit.TypeOrDefault<FlexDuctType>(c, "type", BuiltInCategory.OST_FlexDuctCurves, "flex duct");
            MechanicalSystemType system = MepKit.SystemType<MechanicalSystemType>(c, "system", t => t.SystemClassification == MEPSystemClassification.SupplyAir, "duct");
            List<XYZ> points = FlexPoints(c, level);
            FlexDuct flex = FlexDuct.Create(doc, system.Id, type.Id, level.Id, points);
            if (flex == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the flex duct.");
            c.Created(flex);
            double? size = c.Args.MmOpt("size");
            if (size.HasValue) MepKit.SetSize(c, flex, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, size.Value, "diameter");
            c.EnsureRegenerated();
            c.Output("type", type.Id);
            c.Output("level", level.Id);
            c.Summary("created flex duct (" + Resolve.FullTypeName(type) + ", " + system.Name + ") on " + level.Name + " through " + ModelKit.Count(points.Count, "point") +
                      (size.HasValue ? ", dia " + ModelKit.Mm(size.Value) + " mm" : string.Empty));
        }

        // -------------------------------------------------------------------------------------------------------------
        // Shared run builder
        // -------------------------------------------------------------------------------------------------------------

        private sealed class RunResult
        {
            public List<MEPCurve> Segments = new List<MEPCurve>();
            public List<FamilyInstance> Fittings = new List<FamilyInstance>();
            public int FailedFittings;
            public double Length;
        }

        private static RunResult BuildRun(ChangeContext c, List<XYZ> raw, Func<XYZ, XYZ, MEPCurve> make, Action<MEPCurve> size)
        {
            Document doc = c.Doc;
            List<XYZ> points = MepKit.Simplify(raw);
            if (points.Count < 2) throw OpException.InvalidArgs(c.Args.Field("points"), "needs at least 2 distinct points");
            if (points.Count - 1 > MaxSegments) throw OpException.InvalidArgs(c.Args.Field("points"), "makes at most " + MaxSegments + " segments per call");
            bool fittings = c.Bool("fittings", true);
            var result = new RunResult();
            double tolerance = Math.Max(doc.Application.ShortCurveTolerance, 1e-6);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (points[i].DistanceTo(points[i + 1]) <= tolerance)
                {
                    throw OpException.InvalidArgs(c.Args.Field("points"), "segment " + i + " is shorter than Revit's minimum length");
                }
                MEPCurve curve = make(points[i], points[i + 1]) ?? throw new OpException(ErrorCodes.RevitRefused, "Revit did not create segment " + i.ToString(CultureInfo.InvariantCulture) + ".");
                c.Created(curve);
                size(curve);
                result.Segments.Add(curve);
                result.Length += points[i].DistanceTo(points[i + 1]);
            }
            c.EnsureRegenerated();
            if (!fittings || result.Segments.Count < 2) return result;

            for (int i = 0; i + 1 < result.Segments.Count; i++)
            {
                XYZ corner = points[i + 1];
                Connector a = MepKit.Nearest(result.Segments[i], corner, unconnectedOnly: true);
                Connector b = MepKit.Nearest(result.Segments[i + 1], corner, unconnectedOnly: true);
                if (a == null || b == null)
                {
                    result.FailedFittings++;
                    c.Warn(WarningCodes.FittingFailed, "no free connectors at bend " + (i + 1) + " " + ModelKit.Pt(corner), new[] { result.Segments[i].Id, result.Segments[i + 1].Id });
                    continue;
                }
                FamilyInstance fitting = MepKit.TryFitting(doc, a, b, out string error);
                if (fitting == null)
                {
                    result.FailedFittings++;
                    c.Warn(WarningCodes.FittingFailed, "no fitting at bend " + (i + 1) + " " + ModelKit.Pt(corner) + ": " + error + " (check the routing preferences of the type; segments stay unconnected)",
                        new[] { result.Segments[i].Id, result.Segments[i + 1].Id });
                    continue;
                }
                c.Created(fitting);
                result.Fittings.Add(fitting);
            }
            return result;
        }

        private static void Finish(ChangeContext c, RunResult run, string what, string typeName, string systemName, Level level, string sizeText)
        {
            c.Output("level", level.Id);
            c.Data(new Dictionary<string, object>
            {
                ["segments"] = run.Segments.Select(s => (object)s.Id.Value).ToList(),
                ["fittings"] = run.Fittings.Select(f => (object)f.Id.Value).ToList(),
                ["failed_fittings"] = run.FailedFittings,
                ["length"] = Units.Mm(run.Length)
            });
            c.Summary("created " + ModelKit.Count(run.Segments.Count, what + " segment") + " (" + typeName + (systemName != null ? ", " + systemName : string.Empty) +
                      (sizeText != null ? ", " + sizeText + " mm" : string.Empty) + ") on " + level.Name + ", " + ModelKit.Mm(run.Length) + " mm" +
                      (run.Fittings.Count > 0 ? ", " + ModelKit.Count(run.Fittings.Count, "fitting") : string.Empty) +
                      (run.FailedFittings > 0 ? ", " + run.FailedFittings.ToString(CultureInfo.InvariantCulture) + " bend(s) without fitting" : string.Empty));
        }

        /// <summary>Pipe path with the z rule; a slope (percent) lowers points without an explicit z along the run.</summary>
        private static List<XYZ> PathWithSlope(ChangeContext c, Level level)
        {
            double offset = c.Mm("offset", DefaultOffsetMm);
            double slope = c.Args.Pct("slope", 0);
            List<XYZ> raw = c.Args.Points("points", PayloadReader.NoZ);
            bool anyZ = raw.Any(p => !PayloadReader.IsNoZ(p.Z));
            if (Math.Abs(slope) > 1e-9 && anyZ)
            {
                c.Warn(WarningCodes.ParamIgnored, "slope is ignored because points carry z values");
                slope = 0;
            }
            if (Math.Abs(slope) > 100) throw OpException.InvalidArgs(c.Args.Field("slope"), "must be between -100 and 100 percent");
            double baseZ = ModelKit.Z(level);
            var result = new List<XYZ>(raw.Count);
            double z = baseZ + offset;
            for (int i = 0; i < raw.Count; i++)
            {
                XYZ p = raw[i];
                if (!PayloadReader.IsNoZ(p.Z))
                {
                    result.Add(new XYZ(p.X, p.Y, baseZ + p.Z));
                    continue;
                }
                if (i > 0) z -= slope / 100.0 * Geometry.DistanceXY(raw[i - 1], p);
                result.Add(new XYZ(p.X, p.Y, z));
            }
            return result;
        }

        private static List<XYZ> FlexPoints(ChangeContext c, Level level)
        {
            List<XYZ> points = ModelKit.Points3(c, "points", level, c.Mm("offset", DefaultOffsetMm));
            var clean = new List<XYZ>();
            foreach (XYZ p in points) if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > Geometry.MinSegmentFeet) clean.Add(p);
            if (clean.Count < 2) throw OpException.InvalidArgs(c.Args.Field("points"), "needs at least 2 distinct points");
            return clean;
        }

        /// <summary>The named duct type, else the first type of the wanted shape (round for size, rectangular for width+height).</summary>
        private static DuctType DuctTypeFor(ChangeContext c, bool? round)
        {
            if (c.Args.Has("type"))
            {
                DuctType named = Resolve.Type<DuctType>(c.Doc, c.Str("type"), c.Args.Field("type"));
                if (round.HasValue && (named.Shape == ConnectorProfileType.Round) != round.Value)
                {
                    c.Warn(WarningCodes.ParamIgnored, "duct type '" + Resolve.FullTypeName(named) + "' is " + named.Shape + "; " + (round.Value ? "size" : "width/height") + " may not apply");
                }
                return named;
            }
            List<DuctType> types = new FilteredElementCollector(c.Doc).OfClass(typeof(DuctType)).Cast<DuctType>().OrderBy(t => Resolve.FullTypeName(t), StringComparer.OrdinalIgnoreCase).ToList();
            DuctType match = round.HasValue
                ? types.FirstOrDefault(t => (t.Shape == ConnectorProfileType.Round) == round.Value)
                : Resolve.DefaultType<DuctType>(c.Doc, BuiltInCategory.OST_DuctCurves);
            return match ?? types.FirstOrDefault() ?? throw new OpException(ErrorCodes.NotFound, "The document has no duct type; use a template with MEP content.",
                new Dictionary<string, object> { ["param"] = c.Args.Field("type"), ["kind"] = "duct type", ["value"] = null, ["candidates"] = new List<string>() });
        }
    }
}
