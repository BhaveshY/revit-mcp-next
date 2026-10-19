using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// modify_elements geometry edits: split (SPEC §5.9: MEP curves via BreakCurve; walls, structural framing and lines by
    /// copy + trim with hosted inserts kept on the half that contains them; an insert spanning the split point is
    /// refused) and reshape (new location curve: line start→end, arc through a point, or 2-3 points).
    /// </summary>
    internal static partial class ModifyElementsOps
    {
        /// <summary>The split point may be at most this far (mm, in plan) from the element's location line.</summary>
        private const double MaxSplitDistanceMm = 1000;

        // -------------------------------------------------------------------------------------------------------------
        // split
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.split")]
        public static void Split(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a, allowFrom: false, allowFilter: false);
            ModifySupport.RefusePinned(c, a, elements, "split");
            XYZ at = a.Point("at");

            var pairs = new List<object>();
            var skipped = new List<List<object>>();
            var replaced = new List<List<object>>();
            foreach (Element element in elements)
            {
                if (!(element.Location is LocationCurve location))
                {
                    skipped.Add(ModifySupport.Skip(element.Id, "it has no location line"));
                    continue;
                }
                Curve curve = location.Curve;
                double raw = SplitParameter(c, a, element, curve, at);
                XYZ point = curve.Evaluate(raw, false);

                ElementId created;
                List<List<object>> replacedHere = null;
                if (element is Pipe)
                {
                    created = PlumbingUtils.BreakCurve(c.Doc, element.Id, point);
                }
                else if (element is Duct)
                {
                    created = MechanicalUtils.BreakCurve(c.Doc, element.Id, point);
                }
                else if (element is MEPCurve)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, "Revit has no API to break " + (Rows.CategoryLabel(element) ?? "this MEP curve") + "; split pipes and ducts only"));
                    continue;
                }
                else if (element is Wall wall)
                {
                    if (wall.WallType.Kind != WallKind.Basic || wall.IsStackedWall)
                    {
                        skipped.Add(ModifySupport.Skip(element.Id, "only basic walls split (curtain and stacked walls do not)"));
                        continue;
                    }
                    created = SplitByCopy(c, element, curve, raw, out replacedHere);
                }
                else if (element is FamilyInstance instance && instance.StructuralType != StructuralType.NonStructural && instance.StructuralType != StructuralType.Footing)
                {
                    created = SplitByCopy(c, element, curve, raw, out replacedHere);
                }
                else if (element is CurveElement)
                {
                    created = SplitByCopy(c, element, curve, raw, out replacedHere);
                }
                else
                {
                    skipped.Add(ModifySupport.Skip(element.Id, "split works on walls, structural framing, pipes, ducts and lines"));
                    continue;
                }

                if (created == null || created == ElementId.InvalidElementId) continue;
                c.Modified(element, "split", created.Value);
                c.Created(created);
                pairs.Add(new List<object> { element.Id.Value, created.Value });
                if (replacedHere != null) replaced.AddRange(replacedHere);
            }

            if (pairs.Count == 0)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Nothing was split: " + Convert.ToString(skipped.FirstOrDefault()?[1], CultureInfo.InvariantCulture) + ".",
                    new Dictionary<string, object> { ["hint"] = Convert.ToString(skipped.FirstOrDefault()?[1], CultureInfo.InvariantCulture) });
            }
            if (pairs.Count == 1) c.Output("second", ((List<object>)pairs[0])[1]);
            var data = new Dictionary<string, object> { ["split"] = pairs, ["at"] = ModifySupport.PointMm(at) };
            // Inserts on the new half are the copy's duplicates: their ids change. [old, new] lets a caller keep its ids.
            if (replaced.Count > 0) data["replaced"] = replaced;
            ModifySupport.ReportSkipped(c, data, skipped, "split");
            c.Data(data);
            c.Summary("split " + ModifySupport.Count(pairs.Count, "element") + " at " + VectorText(ModifySupport.PointMm(at)) + " mm; new halves " +
                      string.Join(", ", pairs.Cast<List<object>>().Select(p => Convert.ToString(p[1], CultureInfo.InvariantCulture))) +
                      (replaced.Count > 0 ? "; " + ModifySupport.Count(replaced.Count, "insert") + " on the new half got new ids (replaced: [old,new])" : string.Empty));
        }

        /// <summary>The raw curve parameter of the split point (projected in plan), validated to lie inside the curve.</summary>
        private static double SplitParameter(ChangeContext c, PayloadReader a, Element element, Curve curve, XYZ at)
        {
            XYZ start = curve.GetEndPoint(0), end = curve.GetEndPoint(1);
            double raw;
            if (curve is Line)
            {
                double t = Geometry.ProjectParameterXY(start, end, at);
                raw = curve.GetEndParameter(0) + t * (curve.GetEndParameter(1) - curve.GetEndParameter(0));
            }
            else
            {
                IntersectionResult projected = curve.Project(new XYZ(at.X, at.Y, (start.Z + end.Z) / 2.0));
                if (projected == null) throw OpException.InvalidArgs(a.Field("at"), "cannot be projected onto element " + element.Id.Value.ToString(CultureInfo.InvariantCulture));
                raw = projected.Parameter;
            }

            XYZ point = curve.Evaluate(Math.Max(curve.GetEndParameter(0), Math.Min(curve.GetEndParameter(1), raw)), false);
            double offMm = Units.FtToMm(Geometry.DistanceXY(point, at));
            if (offMm > MaxSplitDistanceMm)
            {
                throw OpException.InvalidArgs(a.Field("at"), "is " + Math.Round(offMm).ToString(CultureInfo.InvariantCulture) + " mm from element " +
                    element.Id.Value.ToString(CultureInfo.InvariantCulture) + "; give a point on its location line (from " +
                    VectorText(ModifySupport.PointMm(start)) + " to " + VectorText(ModifySupport.PointMm(end)) + ")");
            }

            double tolerance = Math.Max(c.Doc.Application.ShortCurveTolerance * 2, Units.MmToFt(10));
            bool inside = raw > curve.GetEndParameter(0) && raw < curve.GetEndParameter(1);
            if (!inside || DistanceAlong(curve, curve.GetEndParameter(0), raw) < tolerance || DistanceAlong(curve, raw, curve.GetEndParameter(1)) < tolerance)
            {
                throw OpException.InvalidArgs(a.Field("at"), "must lie inside element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) +
                    " (at least 10 mm from both ends of " + VectorText(ModifySupport.PointMm(start)) + " → " + VectorText(ModifySupport.PointMm(end)) + ")");
            }
            return raw;
        }

        private static double DistanceAlong(Curve curve, double from, double to)
        {
            try
            {
                Curve part = curve.Clone();
                part.MakeBound(from, to);
                return part.Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Splits a wall/framing/line by copying it in place and trimming both curves at <paramref name="raw"/>. Hosted
        /// inserts stay on the half that contains them (the copy's duplicates on the other half are deleted); an insert
        /// spanning the split point is refused. Returns the new half.
        /// </summary>
        private static ElementId SplitByCopy(ChangeContext c, Element element, Curve locationCurve, double raw, out List<List<object>> replaced)
        {
            replaced = new List<List<object>>();
            Document doc = c.Doc;
            // Work on an independent copy taken before anything changes: the LocationCurve's curve is owned by the element
            // and goes stale after CopyElement/Regenerate/Delete, and Clone() on it then fails inside Revit (SEHException,
            // seen live on Revit 2027 in e2e S40).
            Curve curve = locationCurve.Clone();
            double p0 = curve.GetEndParameter(0), p1 = curve.GetEndParameter(1);
            List<InsertSpan> inserts = Inserts(doc, element, curve);
            InsertSpan spanning = inserts.FirstOrDefault(i => i.Min < raw - 1e-6 && i.Max > raw + 1e-6);
            if (spanning != null)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "An insert spans the split point; move the split point (" +
                    (Rows.CategoryLabel(doc.GetElement(spanning.Id)) ?? "insert") + " " + spanning.Id.Value.ToString(CultureInfo.InvariantCulture) + " covers it).",
                    new Dictionary<string, object>
                    {
                        ["hint"] = "move the split point outside insert " + spanning.Id.Value.ToString(CultureInfo.InvariantCulture),
                        ["insert"] = spanning.Id.Value,
                        ["from"] = ModifySupport.PointMm(curve.Evaluate(Math.Max(p0, spanning.Min), false)),
                        ["to"] = ModifySupport.PointMm(curve.Evaluate(Math.Min(p1, spanning.Max), false))
                    });
            }

            ICollection<ElementId> copied = ElementTransformUtils.CopyElement(doc, element.Id, XYZ.Zero);
            Element copy = copied.Select(id => doc.GetElement(id))
                .FirstOrDefault(e => e != null && e.GetType() == element.GetType() && e.Category?.Id == element.Category?.Id && e.Location is LocationCurve);
            if (copy == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not copy element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + " for the split.", new Dictionary<string, object> { ["apiMessage"] = "CopyElement returned no curve element" });
            doc.Regenerate();

            List<InsertSpan> copyInserts = Inserts(doc, copy, curve);
            if (copyInserts.Count < inserts.Count)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Revit did not copy the " + inserts.Count.ToString(CultureInfo.InvariantCulture) +
                    " hosted insert(s) of element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + ", so the split would lose them; nothing was changed.",
                    new Dictionary<string, object> { ["hint"] = "split this wall in Revit (Modify > Split Element)" });
            }

            // Each half keeps the inserts on its side of the split point. The original's inserts on the second half are
            // replaced by the copy's duplicates at the same position (same type): report [old, new].
            var used = new HashSet<ElementId>();
            foreach (InsertSpan old in inserts.Where(i => i.Mid > raw))
            {
                ElementId oldType = doc.GetElement(old.Id)?.GetTypeId();
                InsertSpan twin = copyInserts.Where(j => j.Mid > raw && !used.Contains(j.Id) && doc.GetElement(j.Id)?.GetTypeId() == oldType)
                    .OrderBy(j => Math.Abs(j.Mid - old.Mid)).FirstOrDefault();
                if (twin == null) continue;
                used.Add(twin.Id);
                replaced.Add(new List<object> { old.Id.Value, twin.Id.Value });
            }
            var remove = inserts.Where(i => i.Mid > raw).Select(i => i.Id)
                .Concat(copyInserts.Where(i => i.Mid < raw).Select(i => i.Id))
                .Where(id => doc.GetElement(id) != null).ToList();
            if (remove.Count > 0)
            {
                c.Delete(remove);
                doc.Regenerate();
            }

            Curve first = curve.Clone();
            first.MakeBound(p0, raw);
            Curve second = curve.Clone();
            second.MakeBound(raw, p1);
            SetCurve(element, first);
            SetCurve(copy, second);
            foreach (ElementId id in copied)
            {
                if (id != copy.Id && doc.GetElement(id) != null) c.Created(id);
            }
            return copy.Id;
        }

        private sealed class InsertSpan
        {
            public ElementId Id;
            public double Min;
            public double Max;
            public double Mid => (Min + Max) / 2.0;
        }

        /// <summary>Hosted inserts (doors, windows, openings, embedded walls) with their raw-parameter extent along the host curve.</summary>
        private static List<InsertSpan> Inserts(Document doc, Element host, Curve curve)
        {
            var result = new List<InsertSpan>();
            if (!(host is HostObject hostObject)) return result;
            IList<ElementId> ids;
            try { ids = hostObject.FindInserts(true, false, true, false); }
            catch { return result; }
            foreach (ElementId id in ids)
            {
                Element insert = doc.GetElement(id);
                BoundingBoxXYZ box = Geometry.BoxOf(insert);
                var parameters = new List<double>();
                if (box != null)
                {
                    foreach (XYZ corner in Corners(box)) parameters.Add(ParameterOf(curve, corner));
                }
                else if (insert?.Location is LocationPoint point)
                {
                    parameters.Add(ParameterOf(curve, point.Point));
                }
                if (parameters.Count == 0) continue;
                result.Add(new InsertSpan { Id = id, Min = parameters.Min(), Max = parameters.Max() });
            }
            return result;
        }

        private static double ParameterOf(Curve curve, XYZ point)
        {
            if (curve is Line)
            {
                double t = Geometry.ProjectParameterXY(curve.GetEndPoint(0), curve.GetEndPoint(1), point);
                return curve.GetEndParameter(0) + t * (curve.GetEndParameter(1) - curve.GetEndParameter(0));
            }
            XYZ start = curve.GetEndPoint(0);
            IntersectionResult projected = curve.Project(new XYZ(point.X, point.Y, start.Z));
            return projected?.Parameter ?? curve.GetEndParameter(0);
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            Transform transform = box.Transform ?? Transform.Identity;
            foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
                yield return transform.OfPoint(new XYZ(x, y, box.Min.Z));
        }

        private static void SetCurve(Element element, Curve curve)
        {
            if (element is CurveElement line)
            {
                line.SetGeometryCurve(curve, true);
                return;
            }
            ((LocationCurve)element.Location).Curve = curve;
        }

        // -------------------------------------------------------------------------------------------------------------
        // reshape
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.reshape")]
        public static void Reshape(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a, allowFrom: false, allowFilter: false);
            if (elements.Count != 1) throw OpException.InvalidArgs(a.Field("ids"), "reshape changes one element at a time (got " + elements.Count.ToString(CultureInfo.InvariantCulture) + ")");
            Element element = elements[0];
            ModifySupport.RefusePinned(c, a, elements, "reshape");

            if (!(element.Location is LocationCurve location))
            {
                if (element is Floor || element is RoofBase || element is Ceiling || element is Opening)
                {
                    throw new OpException(ErrorCodes.UnsupportedOp, (Rows.CategoryLabel(element) ?? "This element") + " " + element.Id.Value.ToString(CultureInfo.InvariantCulture) +
                        " is sketch-based; its boundary cannot be reshaped in a model transaction.",
                        new Dictionary<string, object> { ["hint"] = "create the new outline with create_elements and delete the old element in one change_set" });
                }
                throw new OpException(ErrorCodes.UnsupportedOp, "Element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + " has no location line to reshape.",
                    new Dictionary<string, object> { ["hint"] = "reshape works on walls, beams, pipes, ducts and lines; move point elements with op move" });
            }

            Curve old = location.Curve;
            Curve curve = NewCurve(a, old);
            if (curve.Length < c.Doc.Application.ShortCurveTolerance) throw OpException.InvalidArgs(a.Field("end"), "makes the element too short");
            List<object> before = new List<object> { ModifySupport.PointMm(old.GetEndPoint(0)), ModifySupport.PointMm(old.GetEndPoint(1)) };
            SetCurve(element, curve);
            c.Modified(element, "curve", before);

            var data = new Dictionary<string, object>
            {
                ["start"] = ModifySupport.PointMm(curve.GetEndPoint(0)),
                ["end"] = ModifySupport.PointMm(curve.GetEndPoint(1)),
                ["length"] = Units.Mm(curve.Length),
                ["arc"] = curve is Arc
            };
            c.Data(data);
            c.Summary("reshaped " + ModifySupport.Describe(elements) + " to " + (curve is Arc ? "an arc " : string.Empty) +
                      VectorText(ModifySupport.PointMm(curve.GetEndPoint(0))) + " → " + VectorText(ModifySupport.PointMm(curve.GetEndPoint(1))) +
                      " (" + Convert.ToString(Units.Mm(curve.Length), CultureInfo.InvariantCulture) + " mm)");
        }

        /// <summary>start+end (+through) or points (2 = line, 3 = arc through the middle one); z defaults to the old ends.</summary>
        private static Curve NewCurve(PayloadReader a, Curve old)
        {
            double z0 = old.GetEndPoint(0).Z, z1 = old.GetEndPoint(1).Z;
            XYZ start, end, through = null;
            if (a.Has("points"))
            {
                if (a.Has("start") || a.Has("end")) throw OpException.InvalidArgs(a.Field("points"), "give either points or start+end");
                List<object> raw = a.List("points");
                List<XYZ> points = a.Points("points", null);
                if (points.Count < 2 || points.Count > 3) throw OpException.InvalidArgs(a.Field("points"), "needs 2 points (line) or 3 points (arc through the middle one), got " + points.Count.ToString(CultureInfo.InvariantCulture));
                start = WithZ(points[0], HasZ(raw, 0), z0);
                end = WithZ(points[points.Count - 1], HasZ(raw, points.Count - 1), z1);
                if (points.Count == 3) through = WithZ(points[1], HasZ(raw, 1), (z0 + z1) / 2.0);
            }
            else
            {
                if (!a.Has("start") || !a.Has("end")) throw OpException.InvalidArgs(a.Field(a.Has("start") ? "end" : "start"), "reshape needs start and end (or points)");
                start = a.PointOpt("start", z0, out _);
                end = a.PointOpt("end", z1, out _);
                if (a.Has("through")) through = a.PointOpt("through", (z0 + z1) / 2.0, out _);
            }
            if (start.DistanceTo(end) < 1e-6) throw OpException.InvalidArgs(a.Field("end"), "must differ from start");
            if (through == null) return Line.CreateBound(start, end);
            try
            {
                return Arc.Create(start, end, through);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw OpException.InvalidArgs(a.Field("through"), "does not define an arc with start and end (" + ex.Message + ")");
            }
        }

        private static bool HasZ(List<object> raw, int index)
        {
            if (index >= raw.Count) return false;
            IList<object> point = RevitMcpNext.Contracts.Wire.AsList(raw[index]);
            if (point != null) return point.Count == 3 && point[2] != null;
            IDictionary<string, object> map = RevitMcpNext.Contracts.Wire.AsMap(raw[index]);
            return map != null && map.ContainsKey("z") && map["z"] != null;
        }

        private static XYZ WithZ(XYZ point, bool hasZ, double z)
        {
            return hasZ ? point : new XYZ(point.X, point.Y, z);
        }
    }
}
