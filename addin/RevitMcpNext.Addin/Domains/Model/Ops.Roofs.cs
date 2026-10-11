using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>create_elements roof (footprint), roof_extrusion and toposolid (D1 §4B, D3 §3.8, SPEC §5.4).</summary>
    internal static class RoofOps
    {
        [Op("create_elements.roof")]
        public static void Roof(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            RoofType type = ModelKit.TypeOrDefault<RoofType>(c, "type", BuiltInCategory.OST_Roofs, "roof");
            double z = ModelKit.Z(level);
            List<XYZ> points = Geometry.NormalizeClosedLoop(ModelKit.FlatPoints(c, "points", z));
            if (points.Count < 3) throw OpException.InvalidArgs(c.Args.Field("points"), "needs at least 3 distinct points for a roof footprint");
            CurveLoop footprint = ModelKit.Loop(doc, points, c.Args.Field("points"));

            double overhang = c.Args.MmOpt("overhang") ?? 0;
            if (Math.Abs(overhang) > 1e-9) footprint = Outward(footprint, points, overhang, c.Args.Field("overhang"));

            double? slopeDegrees = c.Args.DegRawOpt("slope");
            List<int> edges = ModelKit.Ints(c, "slope_edges");
            foreach (int edge in edges)
            {
                if (edge < 0 || edge >= points.Count) throw OpException.InvalidArgs(c.Args.Field("slope_edges"), "edge index " + edge + " is out of range 0-" + (points.Count - 1) + " (edge i runs from points[i] to points[i+1])");
            }
            if (edges.Count > 0 && !slopeDegrees.HasValue) throw OpException.InvalidArgs(c.Args.Field("slope"), "is required with slope_edges (degrees)");

            var curves = new CurveArray();
            foreach (Curve curve in footprint) curves.Append(curve);
            FootPrintRoof roof = doc.Create.NewFootPrintRoof(curves, level, type, out ModelCurveArray mapping);
            if (roof == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the footprint roof.");
            c.Created(roof);

            // V7: SlopeAngle takes Revit's internal slope value (rise/run); SlopeFromDegrees converts through UnitUtils.
            double slope = slopeDegrees.HasValue ? ModelKit.SlopeFromDegrees(slopeDegrees.Value, c.Args.Field("slope")) : 0;
            var sloped = new HashSet<int>(edges.Count > 0 ? edges : (slopeDegrees.HasValue && Math.Abs(slopeDegrees.Value) > 1e-9 ? Enumerable.Range(0, mapping.Size) : Enumerable.Empty<int>()));
            for (int index = 0; index < mapping.Size; index++)
            {
                ModelCurve edge = mapping.get_Item(index);
                bool defines = sloped.Contains(index);
                roof.set_DefinesSlope(edge, defines);
                if (defines) roof.set_SlopeAngle(edge, slope);
            }
            double offset = c.Mm("offset", 0);
            if (Math.Abs(offset) > 1e-9) ModelKit.SetOrWarn(c, roof, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM, offset, "base offset");
            c.EnsureRegenerated();

            ModelKit.Outputs(c, type, level);
            c.Data(new Dictionary<string, object>
            {
                ["type"] = Resolve.FullTypeName(type),
                ["level"] = level.Name,
                ["sloped_edges"] = sloped.OrderBy(i => i).Cast<object>().ToList(),
                ["slope_deg"] = slopeDegrees
            });
            c.Summary("created footprint roof (" + Resolve.FullTypeName(type) + ") on " + level.Name +
                      (sloped.Count > 0 ? ", " + ModelKit.Count(sloped.Count, "sloped edge") + " at " + slopeDegrees.Value.ToString("0.##", CultureInfo.InvariantCulture) + " deg" : ", flat") +
                      (Math.Abs(overhang) > 1e-9 ? ", overhang " + ModelKit.Mm(overhang) + " mm" : string.Empty));
        }

        /// <summary>The footprint offset outward by <paramref name="distance"/> (the side that grows the area).</summary>
        private static CurveLoop Outward(CurveLoop loop, List<XYZ> points, double distance, string param)
        {
            double original = Geometry.PolygonAreaXY(points);
            CurveLoop best = null;
            foreach (double signed in new[] { distance, -distance })
            {
                try
                {
                    CurveLoop candidate = CurveLoop.CreateViaOffset(loop, signed, XYZ.BasisZ);
                    List<XYZ> corners = candidate.Select(curve => curve.GetEndPoint(0)).ToList();
                    double area = Geometry.PolygonAreaXY(corners);
                    if ((distance > 0 && area > original) || (distance < 0 && area < original)) return candidate;
                    best = best ?? candidate;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Try the other side.
                }
            }
            if (best == null) throw OpException.InvalidArgs(param, "the footprint cannot be offset by " + ModelKit.Mm(distance) + " mm");
            return best;
        }

        [Op("create_elements.roof_extrusion")]
        public static void RoofExtrusion(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            RoofType type = ModelKit.TypeOrDefault<RoofType>(c, "type", BuiltInCategory.OST_Roofs, "roof");
            double z = ModelKit.Z(level);
            XYZ start = ModelKit.Flat(c.Point("start"), z);
            XYZ end = ModelKit.Flat(c.Point("end"), z);
            XYZ direction = ModelKit.DirXY(start, end, c.Args.Field("end"));
            XYZ left = ModelKit.LeftOf(direction);
            double depth = c.Mm("depth");
            if (Math.Abs(depth) <= Geometry.MinSegmentFeet) throw OpException.InvalidArgs(c.Args.Field("depth"), "must be greater than 0 mm");

            // profile [[s,z],...]: s along start->end, z above the level (the point's y is the height).
            List<XYZ> raw = c.Args.Points("profile", 0);
            if (raw.Count < 2) throw OpException.InvalidArgs(c.Args.Field("profile"), "needs at least 2 points [[s,z],...]");
            List<XYZ> profilePoints = raw.Select(p => start + direction * p.X + XYZ.BasisZ * p.Y).ToList();
            List<Curve> profile = ModelKit.Polyline(doc, profilePoints, false, c.Args.Field("profile"));

            View view = DatumOps.DefaultSketchView(c);
            ReferencePlane plane = doc.Create.NewReferencePlane(start, end, XYZ.BasisZ, view);
            c.Created(plane);
            double sign = plane.Normal.DotProduct(left) >= 0 ? 1 : -1;
            ExtrusionRoof roof = doc.Create.NewExtrusionRoof(ModelKit.ToArray(profile), plane, level, type, 0, sign * depth);
            if (roof == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the extrusion roof.");
            c.Created(roof);
            c.EnsureRegenerated();

            // Keep the extrusion on the left of start->end whatever the plane normal turned out to be.
            BoundingBoxXYZ box = roof.get_BoundingBox(null);
            if (box != null)
            {
                XYZ center = (box.Min + box.Max) / 2;
                if ((center - start).DotProduct(left) < 0)
                {
                    ModelKit.TrySet(roof, BuiltInParameter.EXTRUSION_END_PARAM, -sign * depth);
                    c.MarkDirty();
                    c.EnsureRegenerated();
                }
            }
            ModelKit.Outputs(c, type, level);
            c.Output("ref_plane", plane.Id);
            c.Summary("created extrusion roof (" + Resolve.FullTypeName(type) + ") on " + level.Name + ", profile of " + ModelKit.Count(profile.Count, "segment") +
                      " along " + ModelKit.Pt(start) + "->" + ModelKit.Pt(end) + ", depth " + ModelKit.Mm(depth) + " mm to the left");
        }

        [Op("create_elements.toposolid")]
        public static void Toposolid(ChangeContext c)
        {
            Document doc = c.Doc;
            Level given = c.Level("level", required: false);
            List<XYZ> outline = Absolute(c, "points", given, required: true);
            List<XYZ> survey = Absolute(c, "survey", given, required: false);
            if (outline.Count < 3) throw OpException.InvalidArgs(c.Args.Field("points"), "needs at least 3 points [[x,y,z],...]");
            Level level = given ?? Resolve.NearestLevel(doc, outline.Min(p => p.Z)) ?? FamilyPlacementOps.DefaultLevel(c, null);
            ToposolidType type = ModelKit.TypeOrDefault<ToposolidType>(c, "type", BuiltInCategory.OST_Toposolid, "toposolid");

            double z = ModelKit.Z(level);
            List<XYZ> flat = outline.Select(p => new XYZ(p.X, p.Y, z)).ToList();
            CurveLoop profile = FloorRoomOps.OrientedLoop(doc, flat, c.Args.Field("points"), counterClockwise: true);
            var profiles = new List<CurveLoop> { profile };

            bool shaped = survey.Count > 0 || outline.Any(p => Math.Abs(p.Z - z) > Units.MmToFt(0.5));
            Toposolid topo = shaped
                ? Autodesk.Revit.DB.Toposolid.Create(doc, profiles, outline.Concat(survey).ToList(), type.Id, level.Id)
                : Autodesk.Revit.DB.Toposolid.Create(doc, profiles, type.Id, level.Id);
            if (topo == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the toposolid.");
            c.Created(topo);
            c.EnsureRegenerated();
            ModelKit.Outputs(c, type, level);
            double min = outline.Concat(survey).Min(p => p.Z), max = outline.Concat(survey).Max(p => p.Z);
            c.Data(new Dictionary<string, object> { ["type"] = Resolve.FullTypeName(type), ["level"] = level.Name, ["z_min"] = Units.Mm(min), ["z_max"] = Units.Mm(max) });
            c.Summary("created toposolid (" + Resolve.FullTypeName(type) + ") on " + level.Name + " from " + ModelKit.Count(outline.Count, "outline point") +
                      (survey.Count > 0 ? " and " + ModelKit.Count(survey.Count, "survey point") : string.Empty) + ", elevations " + ModelKit.Mm(min) + " to " + ModelKit.Mm(max) + " mm");
        }

        /// <summary>SPEC §5.4 z rule: z above <paramref name="level"/> when given, else absolute; a missing z is the level (or 0).</summary>
        private static List<XYZ> Absolute(ChangeContext c, string key, Level level, bool required)
        {
            double baseZ = ModelKit.Z(level);
            return c.Args.Points(key, double.NaN, required)
                .Select(p => new XYZ(p.X, p.Y, double.IsNaN(p.Z) ? baseZ : (level != null ? baseZ + p.Z : p.Z)))
                .ToList();
        }
    }
}
