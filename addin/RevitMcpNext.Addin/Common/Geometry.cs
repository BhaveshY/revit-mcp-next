using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Geometry helpers in Revit internal units (feet): loops, polygons, boxes, projections. Inputs from the wire are
    /// converted by PayloadReader first (mm → feet).
    /// </summary>
    internal static class Geometry
    {
        /// <summary>Revit's short-curve tolerance is about 1/256 ft; segments shorter than this are dropped.</summary>
        public const double MinSegmentFeet = 1.0 / 256.0;

        /// <summary>Removes consecutive duplicates and a closing point equal to the first one.</summary>
        public static List<XYZ> NormalizeClosedLoop(IList<XYZ> points)
        {
            var result = new List<XYZ>();
            if (points == null) return result;
            foreach (XYZ point in points)
            {
                if (point == null) continue;
                if (result.Count == 0 || result[result.Count - 1].DistanceTo(point) > MinSegmentFeet) result.Add(point);
            }
            if (result.Count > 1 && result[0].DistanceTo(result[result.Count - 1]) <= MinSegmentFeet) result.RemoveAt(result.Count - 1);
            return result;
        }

        /// <summary>Closed CurveLoop through the points (at least 3 distinct points).</summary>
        public static CurveLoop BuildCurveLoop(IList<XYZ> points, string param = "points")
        {
            List<XYZ> loop = NormalizeClosedLoop(points);
            if (loop.Count < 3)
            {
                throw OpException.InvalidArgs(param, "needs at least 3 distinct points for a closed outline");
            }
            var curveLoop = new CurveLoop();
            for (int index = 0; index < loop.Count; index++)
            {
                curveLoop.Append(Line.CreateBound(loop[index], loop[(index + 1) % loop.Count]));
            }
            return curveLoop;
        }

        /// <summary>CurveArray of a closed loop (legacy creation APIs).</summary>
        public static CurveArray BuildCurveArray(IList<XYZ> points, string param = "points")
        {
            var array = new CurveArray();
            foreach (Curve curve in BuildCurveLoop(points, param)) array.Append(curve);
            return array;
        }

        /// <summary>Open chain of lines through the points (polyline).</summary>
        public static List<Line> BuildPolyline(IList<XYZ> points, bool closed, string param = "points")
        {
            var clean = new List<XYZ>();
            foreach (XYZ point in points ?? new List<XYZ>())
            {
                if (point != null && (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(point) > MinSegmentFeet)) clean.Add(point);
            }
            if (closed && clean.Count > 2 && clean[0].DistanceTo(clean[clean.Count - 1]) <= MinSegmentFeet) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 2) throw OpException.InvalidArgs(param, "needs at least 2 distinct points");
            var lines = new List<Line>();
            for (int index = 0; index + 1 < clean.Count; index++) lines.Add(Line.CreateBound(clean[index], clean[index + 1]));
            if (closed && clean.Count > 2) lines.Add(Line.CreateBound(clean[clean.Count - 1], clean[0]));
            return lines;
        }

        /// <summary>Signed area of the XY projection (positive = counter-clockwise), in ft².</summary>
        public static double SignedAreaXY(IList<XYZ> points)
        {
            if (points == null || points.Count < 3) return 0;
            double sum = 0;
            for (int index = 0; index < points.Count; index++)
            {
                XYZ a = points[index], b = points[(index + 1) % points.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2.0;
        }

        public static double PolygonAreaXY(IList<XYZ> points) => Math.Abs(SignedAreaXY(points));

        public static bool IsCounterClockwise(IList<XYZ> points) => SignedAreaXY(points) > 0;

        /// <summary>Returns the points in counter-clockwise order (copy).</summary>
        public static List<XYZ> CounterClockwise(IList<XYZ> points)
        {
            var list = points.ToList();
            if (!IsCounterClockwise(list)) list.Reverse();
            return list;
        }

        /// <summary>Axis-aligned XY rectangle [x0,y0,x1,y1] (any corner order) as 4 points at z.</summary>
        public static List<XYZ> Rectangle(double x0, double y0, double x1, double y1, double z = 0)
        {
            double minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1), minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            return new List<XYZ> { new XYZ(minX, minY, z), new XYZ(maxX, minY, z), new XYZ(maxX, maxY, z), new XYZ(minX, maxY, z) };
        }

        /// <summary>Outline from two corners (any order).</summary>
        public static Outline OutlineOf(XYZ a, XYZ b)
        {
            return new Outline(
                new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
                new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        }

        /// <summary>Union of bounding boxes (null when none).</summary>
        public static BoundingBoxXYZ Union(IEnumerable<BoundingBoxXYZ> boxes)
        {
            BoundingBoxXYZ result = null;
            foreach (BoundingBoxXYZ box in boxes ?? Enumerable.Empty<BoundingBoxXYZ>())
            {
                if (box == null) continue;
                XYZ min = box.Transform == null ? box.Min : box.Transform.OfPoint(box.Min);
                XYZ max = box.Transform == null ? box.Max : box.Transform.OfPoint(box.Max);
                if (result == null)
                {
                    result = new BoundingBoxXYZ { Min = Min(min, max), Max = Max(min, max) };
                    continue;
                }
                result.Min = Min(result.Min, Min(min, max));
                result.Max = Max(result.Max, Max(min, max));
            }
            return result;
        }

        public static XYZ Min(XYZ a, XYZ b) => new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

        public static XYZ Max(XYZ a, XYZ b) => new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

        /// <summary>Parameter (0..1) of the projection of <paramref name="point"/> onto the segment a-b in XY (unclamped).</summary>
        public static double ProjectParameterXY(XYZ a, XYZ b, XYZ point)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-12) return 0;
            return ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
        }

        /// <summary>Projects a point onto an (unbounded) curve; returns the closest point (or the input when it fails).</summary>
        public static XYZ ProjectOnto(Curve curve, XYZ point)
        {
            try
            {
                IntersectionResult result = curve.Project(point);
                return result?.XYZPoint ?? point;
            }
            catch
            {
                return point;
            }
        }

        /// <summary>XY distance between two points (feet).</summary>
        public static double DistanceXY(XYZ a, XYZ b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>The bounding box of an element in model coordinates (null when none).</summary>
        public static BoundingBoxXYZ BoxOf(Element element, View view = null)
        {
            try
            {
                return element?.get_BoundingBox(view);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Location point or curve midpoint of an element (null when it has no location).</summary>
        public static XYZ AnchorOf(Element element)
        {
            switch (element?.Location)
            {
                case LocationPoint point: return point.Point;
                case LocationCurve curve: return curve.Curve.Evaluate(0.5, true);
                default:
                    BoundingBoxXYZ box = BoxOf(element);
                    return box == null ? null : (box.Min + box.Max) / 2.0;
            }
        }

        public static bool IsFinite(XYZ point)
        {
            return point != null && Units.IsFinite(point.X) && Units.IsFinite(point.Y) && Units.IsFinite(point.Z);
        }
    }
}
