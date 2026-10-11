using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// modify_elements transforms (SPEC Appendix A/B, D3 §3): move, copy (by vector or to levels), rotate, mirror,
    /// array (linear/radial, unassociated copies), align. All run inside the op's transaction (tx "in"); the engine
    /// evaluates the bulk rule from the recorded modified/created sets. Inputs are mm/deg (ModifySupport.Args applies
    /// the per-call units), outputs mm.
    /// </summary>
    internal static partial class ModifyElementsOps
    {
        private const int MaxCopies = 500;
        private const double Tolerance = 1e-6;

        // -------------------------------------------------------------------------------------------------------------
        // move
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.move")]
        public static void Move(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            var skipped = new List<List<object>>();
            List<Element> elements = Transformable(c, ModifySupport.Targets(c, a), skipped, "moved");
            ModifySupport.RefusePinned(c, a, elements, "move");
            XYZ vector = Translation(a, elements);
            if (vector.GetLength() < Tolerance) throw OpException.InvalidArgs(a.Field(a.Has("by") ? "by" : "to"), "gives a zero move; the elements are already there");

            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            ModifySupport.WithUnpinned(elements, () => ElementTransformUtils.MoveElements(c.Doc, ids, vector));
            List<object> mm = ModifySupport.PointMm(vector);
            foreach (Element element in elements) c.Modified(element, "move", mm);

            var data = new Dictionary<string, object> { ["moved"] = elements.Count, ["by"] = mm };
            ModifySupport.ReportSkipped(c, data, skipped, "moved");
            c.Data(data);
            c.Summary("moved " + ModifySupport.Describe(elements) + " by " + VectorText(mm) + " mm");
        }

        // -------------------------------------------------------------------------------------------------------------
        // copy
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.copy")]
        public static void Copy(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            var skipped = new List<List<object>>();
            List<Element> elements = Transformable(c, ModifySupport.Targets(c, a), skipped, "copied");
            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            var data = new Dictionary<string, object>();

            if (a.Has("levels"))
            {
                if (a.Has("by") || a.Has("to")) throw OpException.InvalidArgs(a.Field("levels"), "give only one of by, to or levels");
                CopyToLevels(c, a, elements, data);
            }
            else
            {
                XYZ vector = Translation(a, elements);
                if (vector.GetLength() < Tolerance) throw OpException.InvalidArgs(a.Field(a.Has("by") ? "by" : "to"), "gives a zero offset; copies would sit on the originals");
                int count = a.IntClamped("count", 1, 1, MaxCopies, (code, text) => c.Warn(code, text));
                var created = new List<ElementId>();
                for (int index = 1; index <= count; index++)
                {
                    ICollection<ElementId> copies = ElementTransformUtils.CopyElements(c.Doc, ids, vector.Multiply(index));
                    created.AddRange(copies);
                    if (index == 1 && copies.Count > 0) c.Output("first", copies.First());
                }
                c.Created(created);
                data["copies"] = count;
                data["by"] = ModifySupport.PointMm(vector);
                c.Summary("copied " + ModifySupport.Describe(elements) + " " + ModifySupport.Count(count, "time") + " by " +
                          VectorText(ModifySupport.PointMm(vector)) + " mm (" + ModifySupport.Count(created.Count, "new element") + ")");
            }

            ModifySupport.ReportSkipped(c, data, skipped, "copied");
            c.Data(data);
        }

        /// <summary>
        /// Copies to each target level with a vertical offset (the lowest source level maps to the target level), then
        /// re-associates the level constraints of every copy (base/top/reference level + offsets) like Revit's
        /// "Paste Aligned to Selected Levels".
        /// </summary>
        private static void CopyToLevels(ChangeContext c, PayloadReader a, List<Element> elements, Dictionary<string, object> data)
        {
            List<string> names = a.Strs("levels");
            if (names.Count == 0) throw OpException.InvalidArgs(a.Field("levels"), "needs at least one level name");
            List<Level> targets = names.Select(name => Resolve.Level(c.Doc, name, a.Field("levels"), c.Ctx.UiDoc)).GroupBy(l => l.Id.Value).Select(g => g.First()).ToList();
            Level reference = elements.Select(ModifySupport.LevelOf).Where(l => l != null).OrderBy(l => l.ProjectElevation).FirstOrDefault();
            if (reference == null)
            {
                throw OpException.InvalidArgs(a.Field("levels"), "the elements have no level to align from; copy them with by:[dx,dy,dz] instead",
                    new Dictionary<string, object> { ["op"] = "copy", ["ids"] = elements.Take(3).Select(e => (object)e.Id.Value).ToList(), ["by"] = new List<object> { 0, 0, 3000 } });
            }

            List<Level> allLevels = new FilteredElementCollector(c.Doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            var created = new List<ElementId>();
            var perLevel = new List<object>();
            int unmapped = 0;
            foreach (Level target in targets)
            {
                double dz = target.ProjectElevation - reference.ProjectElevation;
                if (Math.Abs(dz) < Tolerance)
                {
                    c.Warn(WarningCodes.PartialResult, "skipped level '" + target.Name + "': it is the source level");
                    continue;
                }
                var vector = new XYZ(0, 0, dz);
                ICollection<ElementId> copies = ElementTransformUtils.CopyElements(c.Doc, ids, vector);
                created.AddRange(copies);
                c.Doc.Regenerate();
                unmapped += Releveled(c, elements, copies, dz, allLevels);
                perLevel.Add(new List<object> { target.Name, copies.Count });
            }
            if (created.Count == 0) throw OpException.InvalidArgs(a.Field("levels"), "names only the source level '" + reference.Name + "'; give other levels");
            c.Created(created);
            if (created.Count > 0) c.Output("first", created[0]);
            if (unmapped > 0)
            {
                c.Warn(WarningCodes.PartialResult, unmapped.ToString(CultureInfo.InvariantCulture) +
                    " level constraint(s) had no matching level at the copy height and keep their original level with an offset");
            }
            data["levels"] = perLevel;
            data["from_level"] = reference.Name;
            c.Summary("copied " + ModifySupport.Describe(elements) + " from " + reference.Name + " to " +
                      string.Join(", ", targets.Where(t => Math.Abs(t.ProjectElevation - reference.ProjectElevation) >= Tolerance).Select(t => t.Name)) +
                      " (" + ModifySupport.Count(created.Count, "new element") + ")");
        }

        /// <summary>Level/offset parameter pairs re-associated after a copy to levels (offset null = keep).</summary>
        private static readonly BuiltInParameter[][] LevelPairs =
        {
            new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.WALL_BASE_OFFSET },
            new[] { BuiltInParameter.WALL_HEIGHT_TYPE, BuiltInParameter.WALL_TOP_OFFSET },
            new[] { BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM },
            new[] { BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM },
            new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM },
            new[] { BuiltInParameter.LEVEL_PARAM, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM },
            new[] { BuiltInParameter.LEVEL_PARAM, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM },
            new[] { BuiltInParameter.ROOF_BASE_LEVEL_PARAM, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM },
            new[] { BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, BuiltInParameter.STAIRS_BASE_OFFSET },
            new[] { BuiltInParameter.STAIRS_TOP_LEVEL_PARAM, BuiltInParameter.STAIRS_TOP_OFFSET },
        };

        /// <summary>Matches each copy to its source (category, type, location + dz) and moves its level constraints up.</summary>
        private static int Releveled(ChangeContext c, List<Element> originals, ICollection<ElementId> copies, double dz, List<Level> levels)
        {
            int unmapped = 0;
            var pool = copies.Select(id => c.Doc.GetElement(id)).Where(e => e != null).ToList();
            foreach (Element original in originals)
            {
                if (!(original.Location is LocationPoint) && !(original.Location is LocationCurve) && !HasLevelPair(original)) continue;
                Element copy = pool.FirstOrDefault(candidate => SameShape(original, candidate, dz));
                if (copy == null) continue;
                pool.Remove(copy);
                foreach (BuiltInParameter[] pair in LevelPairs)
                {
                    Parameter sourceLevel = SafeParameter(original, pair[0]);
                    Parameter copyLevel = SafeParameter(copy, pair[0]);
                    if (sourceLevel == null || copyLevel == null || copyLevel.IsReadOnly || sourceLevel.StorageType != StorageType.ElementId) continue;
                    if (!(c.Doc.GetElement(sourceLevel.AsElementId()) is Level from)) continue;
                    Level to = levels.FirstOrDefault(l => Math.Abs(l.ProjectElevation - (from.ProjectElevation + dz)) < Units.MmToFt(0.5));
                    if (to == null)
                    {
                        unmapped++;
                        continue;
                    }
                    Parameter sourceOffset = SafeParameter(original, pair[1]);
                    double? offset = sourceOffset != null && sourceOffset.StorageType == StorageType.Double ? sourceOffset.AsDouble() : (double?)null;
                    try
                    {
                        if (copyLevel.AsElementId() != to.Id) copyLevel.Set(to.Id);
                        Parameter copyOffset = SafeParameter(copy, pair[1]);
                        if (offset.HasValue && copyOffset != null && !copyOffset.IsReadOnly) copyOffset.Set(offset.Value);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        unmapped++;
                    }
                }
            }
            return unmapped;
        }

        private static bool HasLevelPair(Element element)
        {
            return LevelPairs.Any(pair => SafeParameter(element, pair[0]) != null);
        }

        private static bool SameShape(Element original, Element copy, double dz)
        {
            if (copy.Category?.Id != original.Category?.Id || copy.GetTypeId() != original.GetTypeId()) return false;
            double tolerance = Units.MmToFt(1);
            switch (original.Location)
            {
                case LocationPoint point when copy.Location is LocationPoint other:
                    return point.Point.Add(new XYZ(0, 0, dz)).DistanceTo(other.Point) < tolerance;
                case LocationCurve curve when copy.Location is LocationCurve other:
                    return curve.Curve.GetEndPoint(0).Add(new XYZ(0, 0, dz)).DistanceTo(other.Curve.GetEndPoint(0)) < tolerance &&
                           curve.Curve.GetEndPoint(1).Add(new XYZ(0, 0, dz)).DistanceTo(other.Curve.GetEndPoint(1)) < tolerance;
                default:
                    BoundingBoxXYZ a = Geometry.BoxOf(original), b = Geometry.BoxOf(copy);
                    return a != null && b != null && a.Min.Add(new XYZ(0, 0, dz)).DistanceTo(b.Min) < tolerance * 10;
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // rotate / mirror
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.rotate")]
        public static void Rotate(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            var skipped = new List<List<object>>();
            List<Element> elements = Transformable(c, ModifySupport.Targets(c, a), skipped, "rotated");
            ModifySupport.RefusePinned(c, a, elements, "rotate");
            double degrees = a.DegRaw("angle");
            if (Math.Abs(degrees) < 1e-9) throw OpException.InvalidArgs(a.Field("angle"), "is 0; give the rotation in degrees (counterclockwise positive)");
            XYZ center = a.PointOpt("center") ?? ModifySupport.CenterOf(elements);
            XYZ axis = ModifySupport.Direction(a, "axis", XYZ.BasisZ);
            Line line = Line.CreateUnbound(center, axis);
            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            ModifySupport.WithUnpinned(elements, () => ElementTransformUtils.RotateElements(c.Doc, ids, line, Units.DegToRad(degrees)));
            foreach (Element element in elements) c.Modified(element, "rotate", new List<object> { degrees, ModifySupport.PointMm(center) });

            var data = new Dictionary<string, object>
            {
                ["rotated"] = elements.Count,
                ["angle"] = Units.Number(degrees, 3),
                ["center"] = ModifySupport.PointMm(center)
            };
            ModifySupport.ReportSkipped(c, data, skipped, "rotated");
            c.Data(data);
            c.Summary("rotated " + ModifySupport.Describe(elements) + " by " + Num(degrees) + "° about " + VectorText(ModifySupport.PointMm(center)));
        }

        [Op("modify_elements.mirror")]
        public static void Mirror(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            var skipped = new List<List<object>>();
            List<Element> elements = Transformable(c, ModifySupport.Targets(c, a), skipped, "mirrored");
            bool keep = a.Bool("keep", true);
            XYZ start = a.Point("start");
            XYZ end = a.Point("end", start.Z);
            var direction = new XYZ(end.X - start.X, end.Y - start.Y, 0);
            if (direction.GetLength() < Units.MmToFt(1)) throw OpException.InvalidArgs(a.Field("end"), "must differ from start in plan; the mirror axis is the line start→end");
            XYZ normal = new XYZ(-direction.Y, direction.X, 0).Normalize();
            Plane plane = Plane.CreateByNormalAndOrigin(normal, start);

            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            if (!ElementTransformUtils.CanMirrorElements(c.Doc, ids))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Revit cannot mirror these elements (" + ModifySupport.IdList(ids) + ").",
                    new Dictionary<string, object> { ["hint"] = "mirror hosts together with their hosted elements, or leave out elements that cannot be mirrored (views, types, pinned groups)" });
            }

            if (keep)
            {
                IList<ElementId> copies = ElementTransformUtils.MirrorElements(c.Doc, ids, plane, true);
                c.Created(copies);
                if (copies.Count > 0) c.Output("first", copies[0]);
                c.Summary("mirrored copies of " + ModifySupport.Describe(elements) + " (" + ModifySupport.Count(copies.Count, "new element") + ")");
            }
            else
            {
                ModifySupport.RefusePinned(c, a, elements, "mirror");
                ModifySupport.WithUnpinned(elements, () => ElementTransformUtils.MirrorElements(c.Doc, ids, plane, false));
                foreach (Element element in elements.Where(e => e.IsValidObject)) c.Modified(element, "mirror", new List<object> { ModifySupport.PointMm(start), ModifySupport.PointMm(end) });
                c.Summary("mirrored " + ModifySupport.Describe(elements) + " in place");
            }

            var data = new Dictionary<string, object>
            {
                ["mirrored"] = elements.Count,
                ["kept_original"] = keep,
                ["axis"] = new List<object> { ModifySupport.PointMm(start), ModifySupport.PointMm(end) }
            };
            ModifySupport.ReportSkipped(c, data, skipped, "mirrored");
            c.Data(data);
        }

        // -------------------------------------------------------------------------------------------------------------
        // array
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.array")]
        public static void Array(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            var skipped = new List<List<object>>();
            List<Element> elements = Transformable(c, ModifySupport.Targets(c, a), skipped, "arrayed");
            int count = a.Int("count");
            if (count < 2) throw OpException.InvalidArgs(a.Field("count"), "must be at least 2 (the count includes the original)");
            if (count > MaxCopies + 1)
            {
                c.Warn(WarningCodes.ValueClamped, a.Field("count") + " " + count.ToString(CultureInfo.InvariantCulture) + " clamped to " + (MaxCopies + 1).ToString(CultureInfo.InvariantCulture));
                count = MaxCopies + 1;
            }

            string kind = a.Enum("kind", a.Has("angle") && !a.Has("by") ? "radial" : "linear", "linear", "radial");
            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            var created = new List<ElementId>();
            var data = new Dictionary<string, object> { ["kind"] = kind, ["count"] = count };

            if (kind == "linear")
            {
                if (!a.Has("by")) throw OpException.InvalidArgs(a.Field("by"), "linear arrays need by:[dx,dy] (spacing between members, mm)");
                if (a.Has("angle")) c.Warn(WarningCodes.ParamIgnored, a.Field("angle") + " is ignored for a linear array");
                XYZ by = a.Vector("by");
                if (by.GetLength() < Tolerance) throw OpException.InvalidArgs(a.Field("by"), "must not be a zero vector");
                for (int index = 1; index < count; index++)
                {
                    created.AddRange(ElementTransformUtils.CopyElements(c.Doc, ids, by.Multiply(index)));
                }
                data["by"] = ModifySupport.PointMm(by);
                c.Summary("arrayed " + ModifySupport.Describe(elements) + " linearly: " + count.ToString(CultureInfo.InvariantCulture) +
                          " members " + VectorText(ModifySupport.PointMm(by)) + " mm apart");
            }
            else
            {
                if (!a.Has("angle")) throw OpException.InvalidArgs(a.Field("angle"), "radial arrays need angle (degrees between members)");
                if (a.Has("by")) c.Warn(WarningCodes.ParamIgnored, a.Field("by") + " is ignored for a radial array");
                double degrees = a.DegRaw("angle");
                if (Math.Abs(degrees) < 1e-9) throw OpException.InvalidArgs(a.Field("angle"), "must not be 0");
                XYZ center = a.PointOpt("center") ?? ModifySupport.CenterOf(elements);
                Line axis = Line.CreateUnbound(center, XYZ.BasisZ);
                for (int index = 1; index < count; index++)
                {
                    ICollection<ElementId> copies = ElementTransformUtils.CopyElements(c.Doc, ids, XYZ.Zero);
                    ElementTransformUtils.RotateElements(c.Doc, copies, axis, Units.DegToRad(degrees * index));
                    created.AddRange(copies);
                }
                data["angle"] = Units.Number(degrees, 3);
                data["center"] = ModifySupport.PointMm(center);
                c.Summary("arrayed " + ModifySupport.Describe(elements) + " radially: " + count.ToString(CultureInfo.InvariantCulture) +
                          " members " + Num(degrees) + "° apart about " + VectorText(ModifySupport.PointMm(center)));
            }

            c.Created(created);
            if (created.Count > 0) c.Output("first", created[0]);
            ModifySupport.ReportSkipped(c, data, skipped, "arrayed");
            c.Data(data);
        }

        // -------------------------------------------------------------------------------------------------------------
        // align
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.align")]
        public static void Align(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a, allowFrom: false, allowFilter: false);
            AlignTarget target = ResolveAlignTarget(c, a);
            elements = elements.Where(e => e.Id != target.Element.Id).ToList();
            if (elements.Count == 0) throw OpException.InvalidArgs(a.Field("ids"), "contains only the target itself");
            ModifySupport.RefusePinned(c, a, elements, "align");
            bool lockIt = a.Bool("lock", false);

            var skipped = new List<List<object>>();
            var moved = new List<Element>();
            int locked = 0;
            View lockView = null;
            foreach (Element element in elements)
            {
                XYZ vector = target.MoveFor(element, out string reason);
                if (vector == null)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, reason));
                    continue;
                }
                if (vector.GetLength() > Tolerance)
                {
                    ModifySupport.WithUnpinned(new[] { element }, () => ElementTransformUtils.MoveElement(c.Doc, element.Id, vector));
                    c.Modified(element, "move", ModifySupport.PointMm(vector));
                }
                moved.Add(element);

                if (!lockIt) continue;
                if (lockView == null) lockView = ModifySupport.WorkingView(c, new[] { element }, planOnly: !target.IsHorizontal);
                c.Doc.Regenerate();
                Dimension alignment = TryLock(c, lockView, target, element);
                if (alignment != null)
                {
                    c.Created(alignment);
                    locked++;
                }
            }
            if (moved.Count == 0)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "None of the elements can be aligned to " + target.Label + ": " + Convert.ToString(skipped[0][1], CultureInfo.InvariantCulture) + ".",
                    new Dictionary<string, object> { ["hint"] = "align elements whose location line is parallel to the target, or point-based elements" });
            }
            if (lockIt && locked < moved.Count)
            {
                c.Warn(WarningCodes.PartialResult, (moved.Count - locked).ToString(CultureInfo.InvariantCulture) +
                    " element(s) were aligned but not locked: Revit offers no matching reference (locks work for family instances on grids and reference planes)",
                    moved.Take(20).Select(e => e.Id));
            }

            var data = new Dictionary<string, object> { ["aligned"] = moved.Count, ["target"] = target.Label };
            if (lockIt) data["locked"] = locked;
            ModifySupport.ReportSkipped(c, data, skipped, "aligned");
            c.Data(data);
            c.Summary("aligned " + ModifySupport.Describe(moved) + " to " + target.Label + (lockIt ? " (" + locked.ToString(CultureInfo.InvariantCulture) + " locked)" : string.Empty));
        }

        /// <summary>Where elements are aligned to: a vertical plane through a line (grid, reference plane, line element) or a level height.</summary>
        private sealed class AlignTarget
        {
            public Element Element;
            public string Label;
            /// <summary>Horizontal target (level or horizontal reference plane): elements move vertically.</summary>
            public bool IsHorizontal;
            public double Z;
            public XYZ Origin;
            public XYZ Direction;
            public Reference Reference;

            /// <summary>The move that puts the element's location on the target; null with a reason when it cannot.</summary>
            public XYZ MoveFor(Element element, out string reason)
            {
                reason = null;
                if (IsHorizontal)
                {
                    double? z = BaseZ(element);
                    if (!z.HasValue)
                    {
                        reason = "it has no location";
                        return null;
                    }
                    return new XYZ(0, 0, Z - z.Value);
                }

                XYZ normal = new XYZ(-Direction.Y, Direction.X, 0);
                XYZ point;
                switch (element.Location)
                {
                    case LocationCurve curve when curve.Curve is Line line:
                        XYZ lineDirection = new XYZ(line.Direction.X, line.Direction.Y, 0);
                        if (lineDirection.GetLength() < 1e-9 || Math.Abs(lineDirection.Normalize().CrossProduct(Direction).Z) > 1e-3)
                        {
                            reason = "its location line is not parallel to " + Label;
                            return null;
                        }
                        point = line.GetEndPoint(0);
                        break;
                    case LocationCurve _:
                        reason = "it is curved";
                        return null;
                    case LocationPoint location:
                        point = location.Point;
                        break;
                    default:
                        point = Geometry.AnchorOf(element);
                        if (point == null)
                        {
                            reason = "it has no location";
                            return null;
                        }
                        break;
                }
                double distance = (Origin - point).DotProduct(normal);
                return normal.Multiply(distance);
            }

            private static double? BaseZ(Element element)
            {
                switch (element.Location)
                {
                    case LocationPoint point: return point.Point.Z;
                    case LocationCurve curve: return Math.Min(curve.Curve.GetEndPoint(0).Z, curve.Curve.GetEndPoint(1).Z);
                    default: return Geometry.BoxOf(element)?.Min.Z;
                }
            }
        }

        private static AlignTarget ResolveAlignTarget(ChangeContext c, PayloadReader a)
        {
            string value = a.Str("target") ?? throw OpException.InvalidArgs(a.Field("target"), "is required (a grid, level or reference plane name, or an element id)");
            string param = a.Field("target");
            Element element = Resolve.ById<Element>(c.Doc, value);
            if (element == null)
            {
                List<Element> named = new FilteredElementCollector(c.Doc).WherePasses(new ElementMulticlassFilter(new List<Type> { typeof(Grid), typeof(Level), typeof(ReferencePlane) }))
                    .Where(e => Resolve.Eq(e.Name, value)).ToList();
                if (named.Count > 1) throw OpException.Ambiguous(param, "alignment target", value, named.Select(e => e.Name + " (" + e.GetType().Name + ", id " + e.Id.Value.ToString(CultureInfo.InvariantCulture) + ")"));
                if (named.Count == 0)
                {
                    IEnumerable<string> names = new FilteredElementCollector(c.Doc).WherePasses(new ElementMulticlassFilter(new List<Type> { typeof(Grid), typeof(Level), typeof(ReferencePlane) }))
                        .Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n));
                    throw OpException.NotFound(param, "grid, level or reference plane", value, Resolve.Closest(value, names));
                }
                element = named[0];
            }

            var target = new AlignTarget { Element = element };
            switch (element)
            {
                case Grid grid:
                    if (!(grid.Curve is Line gridLine)) throw new OpException(ErrorCodes.UnsupportedOp, "Grid '" + grid.Name + "' is curved; align supports straight grids.", new Dictionary<string, object> { ["hint"] = "move the elements with by:[dx,dy] instead" });
                    target.Label = "grid " + grid.Name;
                    target.Origin = gridLine.GetEndPoint(0);
                    target.Direction = Flat(gridLine.Direction, param);
                    target.Reference = new Reference(grid);
                    break;
                case Level level:
                    target.Label = "level " + level.Name;
                    target.IsHorizontal = true;
                    target.Z = level.ProjectElevation;
                    target.Reference = new Reference(level);
                    break;
                case ReferencePlane plane:
                    target.Label = "reference plane " + (string.IsNullOrWhiteSpace(plane.Name) ? plane.Id.Value.ToString(CultureInfo.InvariantCulture) : plane.Name);
                    target.Reference = plane.GetReference();
                    if (Math.Abs(plane.Normal.Z) > 0.999)
                    {
                        target.IsHorizontal = true;
                        target.Z = plane.BubbleEnd.Z;
                    }
                    else if (Math.Abs(plane.Normal.Z) < 1e-6)
                    {
                        target.Origin = plane.BubbleEnd;
                        target.Direction = new XYZ(-plane.Normal.Y, plane.Normal.X, 0).Normalize();
                    }
                    else
                    {
                        throw new OpException(ErrorCodes.UnsupportedOp, "The reference plane is inclined; align supports vertical and horizontal planes.", new Dictionary<string, object> { ["hint"] = "move the elements with by:[dx,dy,dz] instead" });
                    }
                    break;
                default:
                    if (element.Location is LocationCurve curve && curve.Curve is Line line)
                    {
                        target.Label = (Rows.CategoryLabel(element) ?? "element") + " " + element.Id.Value.ToString(CultureInfo.InvariantCulture);
                        target.Origin = line.GetEndPoint(0);
                        target.Direction = Flat(line.Direction, param);
                        break;
                    }
                    throw new OpException(ErrorCodes.UnsupportedOp, "Element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + " has no straight location line to align to.",
                        new Dictionary<string, object> { ["hint"] = "align to a grid, level, reference plane or a straight wall/line" });
            }
            return target;
        }

        private static XYZ Flat(XYZ direction, string param)
        {
            var flat = new XYZ(direction.X, direction.Y, 0);
            if (flat.GetLength() < 1e-9) throw OpException.InvalidArgs(param, "is vertical; align needs a target line in plan");
            return flat.Normalize();
        }

        /// <summary>A locked alignment between the target and a parallel center reference of a family instance (null when Revit refuses).</summary>
        private static Dimension TryLock(ChangeContext c, View view, AlignTarget target, Element element)
        {
            if (target.Reference == null || view == null || !(element is FamilyInstance instance)) return null;
            var candidates = new List<Reference>();
            foreach (FamilyInstanceReferenceType type in new[] { FamilyInstanceReferenceType.CenterLeftRight, FamilyInstanceReferenceType.CenterFrontBack, FamilyInstanceReferenceType.CenterElevation })
            {
                try { candidates.AddRange(instance.GetReferences(type)); } catch { }
            }
            foreach (Reference reference in candidates)
            {
                using (var attempt = new SubTransaction(c.Doc))
                {
                    attempt.Start();
                    try
                    {
                        Dimension dimension = c.Doc.Create.NewAlignment(view, target.Reference, reference);
                        if (dimension != null)
                        {
                            dimension.IsLocked = true;
                            attempt.Commit();
                            return dimension;
                        }
                        attempt.RollBack();
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        attempt.RollBack();
                    }
                }
            }
            return null;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Shared
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>Elements that can be transformed (types and views cannot); the rest go to <paramref name="skipped"/>.</summary>
        private static List<Element> Transformable(ChangeContext c, List<Element> elements, List<List<object>> skipped, string verb)
        {
            var result = new List<Element>();
            foreach (Element element in elements)
            {
                if (element is ElementType) skipped.Add(ModifySupport.Skip(element.Id, "a type cannot be " + verb));
                else if (element is View) skipped.Add(ModifySupport.Skip(element.Id, "a view cannot be " + verb));
                else if (element.ViewSpecific && element.OwnerViewId == ElementId.InvalidElementId) skipped.Add(ModifySupport.Skip(element.Id, "not placed in a view"));
                else result.Add(element);
            }
            if (result.Count == 0)
            {
                throw OpException.InvalidArgs("ids", "none of the elements can be " + verb + " (" + Convert.ToString(skipped.FirstOrDefault()?[1], CultureInfo.InvariantCulture) + ")");
            }
            return result;
        }

        /// <summary>by (vector) or to (the first element's location point lands there; z kept unless given).</summary>
        private static XYZ Translation(PayloadReader a, List<Element> elements)
        {
            bool hasBy = a.Has("by"), hasTo = a.Has("to");
            if (hasBy && hasTo) throw OpException.InvalidArgs(a.Field("to"), "give only one of by or to");
            if (hasBy) return a.Vector("by");
            if (!hasTo) throw OpException.InvalidArgs(a.Field("by"), "is required: by:[dx,dy(,dz)] mm or to:[x,y(,z)] mm");
            XYZ anchor = ReferencePoint(elements[0]) ?? throw OpException.InvalidArgs(a.Field("to"), "element " + elements[0].Id.Value.ToString(CultureInfo.InvariantCulture) + " has no location point; use by:[dx,dy]");
            XYZ to = a.PointOpt("to", anchor.Z, out bool hasZ);
            return new XYZ(to.X - anchor.X, to.Y - anchor.Y, hasZ ? to.Z - anchor.Z : 0);
        }

        /// <summary>The location point, the start of a location curve, else the bounding-box center.</summary>
        internal static XYZ ReferencePoint(Element element)
        {
            switch (element?.Location)
            {
                case LocationPoint point: return point.Point;
                case LocationCurve curve: return curve.Curve.GetEndPoint(0);
                default: return Geometry.AnchorOf(element);
            }
        }

        private static Parameter SafeParameter(Element element, BuiltInParameter bip)
        {
            try { return element?.get_Parameter(bip); } catch { return null; }
        }

        /// <summary>A number for summaries (invariant culture, 3 decimals at most).</summary>
        private static string Num(double value)
        {
            return Convert.ToString(Units.Number(value, 3), CultureInfo.InvariantCulture);
        }

        private static string VectorText(List<object> mm)
        {
            if (mm == null) return "[]";
            return "[" + string.Join(", ", mm.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))) + "]";
        }
    }
}
