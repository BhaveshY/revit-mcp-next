using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Picks dimension references from elements (annotate dimension with ids): grids, levels and reference planes use
    /// their own reference; family instances their centre or side reference planes; walls their side faces (exterior,
    /// both, the core boundaries) or centre line; anything else its outermost planar faces across the dimension line.
    /// When a requested kind is not available the faces are used and a PARTIAL_RESULT warning says so.
    /// </summary>
    internal static class DimensionReferences
    {
        private const double ParallelTolerance = 0.999;
        private const double PositionTolerance = 0.5 / 304.8;

        public static List<Reference> For(Element element, View view, XYZ direction, string mode, ChangeContext c)
        {
            switch (element)
            {
                case Grid grid:
                    return new List<Reference> { new Reference(grid) };
                case Level level:
                    return new List<Reference> { level.GetPlaneReference() };
                case ReferencePlane plane:
                    return new List<Reference> { plane.GetReference() };
                case FamilyInstance instance:
                    return ForInstance(instance, view, direction, mode, c);
                case Wall wall:
                    return ForWall(wall, view, direction, mode, c);
                default:
                    return Outermost(element, view, direction, c, "faces");
            }
        }

        private static List<Reference> ForInstance(FamilyInstance instance, View view, XYZ direction, string mode, ChangeContext c)
        {
            XYZ hand = SafeXyz(() => instance.HandOrientation) ?? XYZ.BasisX;
            XYZ facing = SafeXyz(() => instance.FacingOrientation) ?? XYZ.BasisY;
            bool alongHand = Math.Abs(hand.DotProduct(direction)) >= Math.Abs(facing.DotProduct(direction));
            var result = new List<Reference>();
            if (mode == "centers")
            {
                result.AddRange(instance.GetReferences(alongHand ? FamilyInstanceReferenceType.CenterLeftRight : FamilyInstanceReferenceType.CenterFrontBack));
                if (result.Count > 0) return result.Take(1).ToList();
            }
            else
            {
                result.AddRange(instance.GetReferences(alongHand ? FamilyInstanceReferenceType.Left : FamilyInstanceReferenceType.Front));
                result.AddRange(instance.GetReferences(alongHand ? FamilyInstanceReferenceType.Right : FamilyInstanceReferenceType.Back));
                if (result.Count >= 2) return result;
            }
            return Outermost(instance, view, direction, c, mode == "centers" ? "its centre reference plane" : "its side reference planes");
        }

        private static List<Reference> ForWall(Wall wall, View view, XYZ direction, string mode, ChangeContext c)
        {
            XYZ wallDirection = (wall.Location as LocationCurve)?.Curve is Line line ? line.Direction : null;
            bool across = wallDirection == null || Math.Abs(wallDirection.DotProduct(direction)) < 0.1;
            if (!across)
            {
                // The wall runs along the dimension line: dimension its ends.
                return Outermost(wall, view, direction, c, null);
            }

            List<Reference> exterior = SideFaces(wall, ShellLayerType.Exterior);
            List<Reference> interior = SideFaces(wall, ShellLayerType.Interior);
            switch (mode)
            {
                case "exterior":
                    if (exterior.Count > 0) return exterior.Take(1).ToList();
                    break;
                case "faces":
                    if (exterior.Count > 0 && interior.Count > 0) return new List<Reference> { exterior[0], interior[0] };
                    break;
                case "core":
                {
                    List<Reference> core = CoreFaces(wall, view, direction, exterior);
                    if (core.Count == 2) return core;
                    c.Warn(WarningCodes.PartialResult, "wall " + Id(wall) + ": core boundary faces are not available; dimensioned to its faces", new[] { wall.Id });
                    if (exterior.Count > 0 && interior.Count > 0) return new List<Reference> { exterior[0], interior[0] };
                    break;
                }
                default:
                {
                    Reference center = CenterLine(wall, view, direction);
                    if (center != null) return new List<Reference> { center };
                    c.Warn(WarningCodes.PartialResult, "wall " + Id(wall) + ": the centre line has no dimension reference here; dimensioned to its faces", new[] { wall.Id });
                    if (exterior.Count > 0 && interior.Count > 0) return new List<Reference> { exterior[0], interior[0] };
                    break;
                }
            }
            return Outermost(wall, view, direction, c, null);
        }

        private static List<Reference> SideFaces(Wall wall, ShellLayerType side)
        {
            try
            {
                return HostObjectUtils.GetSideFaces(wall, side).Where(r => r != null).ToList();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return new List<Reference>();
            }
        }

        /// <summary>A non-visible location-line curve of the wall with a reference (present in plan geometry of most walls).</summary>
        private static Reference CenterLine(Wall wall, View view, XYZ direction)
        {
            if (!((wall.Location as LocationCurve)?.Curve is Line location)) return null;
            // The location line is the wall centre only for the default location line; prefer the geometric centre.
            double halfway = Mid(location).DotProduct(direction);
            List<Reference> sides = SideFaces(wall, ShellLayerType.Exterior);
            List<Reference> others = SideFaces(wall, ShellLayerType.Interior);
            if (sides.Count > 0 && others.Count > 0)
            {
                PlanarFace a = wall.GetGeometryObjectFromReference(sides[0]) as PlanarFace;
                PlanarFace b = wall.GetGeometryObjectFromReference(others[0]) as PlanarFace;
                if (a != null && b != null) halfway = (a.Origin.DotProduct(direction) + b.Origin.DotProduct(direction)) / 2.0;
            }
            foreach (Tuple<GeometryObject, Transform> item in Walk(wall, view))
            {
                if (!(item.Item1 is Line curve) || curve.Reference == null) continue;
                XYZ curveDirection = item.Item2.OfVector(curve.Direction);
                if (Math.Abs(curveDirection.DotProduct(direction)) > 1 - ParallelTolerance) continue;
                double position = item.Item2.OfPoint(Mid(curve)).DotProduct(direction);
                if (Math.Abs(position - halfway) <= PositionTolerance) return curve.Reference;
            }
            return null;
        }

        /// <summary>The two planar faces at the core boundaries (needs layer solids in the wall geometry).</summary>
        private static List<Reference> CoreFaces(Wall wall, View view, XYZ direction, List<Reference> exterior)
        {
            var result = new List<Reference>();
            CompoundStructure structure = wall.WallType?.GetCompoundStructure();
            if (structure == null || exterior.Count == 0) return result;
            PlanarFace outer = wall.GetGeometryObjectFromReference(exterior[0]) as PlanarFace;
            if (outer == null) return result;
            int first = structure.GetFirstCoreLayerIndex();
            int last = structure.GetLastCoreLayerIndex();
            if (first < 0 || last < first) return result;
            IList<CompoundStructureLayer> layers = structure.GetLayers();
            double toCoreStart = 0, toCoreEnd = 0;
            for (int index = 0; index <= last; index++)
            {
                if (index < first) toCoreStart += layers[index].Width;
                toCoreEnd += layers[index].Width;
            }
            // Positions measured from the exterior face inwards (against the exterior face normal).
            double sign = outer.FaceNormal.DotProduct(direction) >= 0 ? -1 : 1;
            double origin = outer.Origin.DotProduct(direction);
            double[] wanted = { origin + sign * toCoreStart, origin + sign * toCoreEnd };
            List<Tuple<Reference, double>> faces = Planar(wall, view, direction);
            foreach (double position in wanted)
            {
                Tuple<Reference, double> match = faces.FirstOrDefault(f => Math.Abs(f.Item2 - position) <= PositionTolerance);
                if (match != null) result.Add(match.Item1);
            }
            return result;
        }

        /// <summary>The first and last planar faces across the dimension line (or its only one).</summary>
        private static List<Reference> Outermost(Element element, View view, XYZ direction, ChangeContext c, string wanted)
        {
            List<Tuple<Reference, double>> faces = Planar(element, view, direction);
            if (faces.Count == 0)
            {
                throw OpException.InvalidArgs("ids", "element " + Id(element) + " (" + (Rows.CategoryLabel(element) ?? element.GetType().Name) +
                                                     ") has no face or reference across the dimension line; pass refs from describe_elements instead");
            }
            if (wanted != null)
            {
                c.Warn(WarningCodes.PartialResult, "element " + Id(element) + ": " + wanted + " not available; dimensioned to its outer faces", new[] { element.Id });
            }
            List<Tuple<Reference, double>> ordered = faces.OrderBy(f => f.Item2).ToList();
            if (ordered.Count == 1 || Math.Abs(ordered[ordered.Count - 1].Item2 - ordered[0].Item2) <= PositionTolerance)
            {
                return new List<Reference> { ordered[0].Item1 };
            }
            return new List<Reference> { ordered[0].Item1, ordered[ordered.Count - 1].Item1 };
        }

        /// <summary>Planar faces with references whose normal is parallel to <paramref name="direction"/>, with their position along it.</summary>
        private static List<Tuple<Reference, double>> Planar(Element element, View view, XYZ direction)
        {
            var result = new List<Tuple<Reference, double>>();
            foreach (Tuple<GeometryObject, Transform> item in Walk(element, view))
            {
                if (!(item.Item1 is Solid solid) || solid.Faces.IsEmpty) continue;
                foreach (Face face in solid.Faces)
                {
                    if (!(face is PlanarFace planar) || planar.Reference == null) continue;
                    XYZ normal = item.Item2.OfVector(planar.FaceNormal);
                    if (Math.Abs(normal.DotProduct(direction)) < ParallelTolerance) continue;
                    result.Add(Tuple.Create(planar.Reference, item.Item2.OfPoint(planar.Origin).DotProduct(direction)));
                }
            }
            return result;
        }

        /// <summary>Geometry objects of an element with the transform to model space (symbol geometry for instances).</summary>
        private static IEnumerable<Tuple<GeometryObject, Transform>> Walk(Element element, View view)
        {
            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
            if (view != null && !(view is ViewSheet)) options.View = view;
            else options.DetailLevel = ViewDetailLevel.Fine;
            GeometryElement geometry;
            try
            {
                geometry = element.get_Geometry(options);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                geometry = null;
            }
            if (geometry == null) yield break;
            foreach (Tuple<GeometryObject, Transform> item in Flatten(geometry, Transform.Identity, 0)) yield return item;
        }

        private static IEnumerable<Tuple<GeometryObject, Transform>> Flatten(GeometryElement geometry, Transform transform, int depth)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is GeometryInstance instance)
                {
                    if (depth > 3) continue;
                    GeometryElement symbol = instance.GetSymbolGeometry();
                    if (symbol == null) continue;
                    foreach (Tuple<GeometryObject, Transform> inner in Flatten(symbol, transform.Multiply(instance.Transform), depth + 1)) yield return inner;
                }
                else
                {
                    yield return Tuple.Create(obj, transform);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Spot references
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The face of <paramref name="host"/> under <paramref name="at"/> as seen in the view (plans: the highest upward
        /// face containing the XY point; other views: the face nearest the viewer), with the point on it.
        /// </summary>
        public static Tuple<Reference, XYZ> SpotFace(Element host, View view, XYZ at)
        {
            XYZ viewDirection = view is ViewPlan ? XYZ.BasisZ : view.ViewDirection.Normalize();
            Tuple<Reference, XYZ> best = null;
            double bestHeight = double.NegativeInfinity;
            foreach (Tuple<GeometryObject, Transform> item in Walk(host, view))
            {
                if (!(item.Item1 is Solid solid) || solid.Faces.IsEmpty) continue;
                Transform inverse = item.Item2.Inverse;
                foreach (Face face in solid.Faces)
                {
                    if (face.Reference == null) continue;
                    XYZ local = inverse.OfPoint(at);
                    IntersectionResult projected;
                    try { projected = face.Project(local); } catch (Autodesk.Revit.Exceptions.ApplicationException) { projected = null; }
                    if (projected == null) continue;
                    XYZ point = item.Item2.OfPoint(projected.XYZPoint);
                    XYZ normal = item.Item2.OfVector(face.ComputeNormal(projected.UVPoint)).Normalize();
                    if (normal.DotProduct(viewDirection) < 0.01) continue;
                    XYZ offset = point - at;
                    XYZ inPlane = offset - offset.DotProduct(viewDirection) * viewDirection;
                    if (inPlane.GetLength() > 1.0 / 304.8) continue;
                    double height = point.DotProduct(viewDirection);
                    if (height > bestHeight)
                    {
                        bestHeight = height;
                        best = Tuple.Create(face.Reference, point);
                    }
                }
            }
            return best;
        }

        private static XYZ Mid(Curve curve)
        {
            if (curve.IsBound) return curve.Evaluate(0.5, true);
            return curve is Line line ? line.Origin : curve.Evaluate(0, false);
        }

        private static XYZ SafeXyz(Func<XYZ> read)
        {
            try { return read(); } catch { return null; }
        }

        private static string Id(Element element)
        {
            return element.Id.Value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
