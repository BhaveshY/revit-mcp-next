using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Shared plumbing of the modify lane (modify_elements, set_parameters, edit_types): the bulk selector
    /// (exactly one of ids / from / filter, D1 §5.6), the pinned guard, unit-aware args, working views and small
    /// output helpers. Everything here runs inside the op's transaction (tx "in").
    /// </summary>
    internal static class ModifySupport
    {
        /// <summary>Tool-specific error codes (contracts/src/catalog/tools/modify_elements.ts errors).</summary>
        public const string ElementsPinned = "ELEMENTS_PINNED";
        public const string ExpectMismatch = "EXPECT_MISMATCH";

        private const int MaxListed = 20;

        // ---------------------------------------------------------------------------------------------------------
        // Args
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The op's args with the per-call <c>units</c> applied to bare lengths. Broker requests keep plain numbers in
        /// the call's units (framework/normalize.ts), while RequestContext builds its reader with factor 1; this reader
        /// honours <c>units</c> so "units":"m" with by:[1,0] moves 1 m. With units absent (or mm) it is c.Args.
        /// </summary>
        public static PayloadReader Args(ChangeContext c)
        {
            PayloadReader args = c.Args;
            if (Math.Abs(args.BareLengthToMm - 1.0) > 1e-12 || !args.Has("units")) return args;
            string units = args.Str("units");
            double? factor = Units.LengthUnitToMm(units);
            if (!factor.HasValue) throw OpException.InvalidArgs(args.Field("units"), "must be one of mm, cm, m, in, ft");
            if (Math.Abs(factor.Value - 1.0) < 1e-12) return args;
            return new PayloadReader(args.Raw, args.Path, factor.Value);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Selector: exactly one of ids / from / filter
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The target elements of a bulk op. ids: numbers or UniqueIds; from: ids expanded by the broker (r#, last) or
        /// "selection"; filter: find_elements keys compiled by ElementQuery. Link filters are refused (LINK_READ_ONLY);
        /// an empty match is NOT_FOUND. Workshared documents: elements borrowed by others give NOT_EDITABLE.
        /// </summary>
        public static List<Element> Targets(ChangeContext c, PayloadReader a, bool allowFrom = true, bool allowFilter = true)
        {
            var given = new List<string>();
            if (a.Has("ids")) given.Add("ids");
            if (allowFrom && a.Has("from")) given.Add("from");
            if (allowFilter && a.Has("filter")) given.Add("filter");
            string choices = "ids" + (allowFrom ? ", from" : string.Empty) + (allowFilter ? ", filter" : string.Empty);
            if (given.Count == 0)
            {
                throw OpException.InvalidArgs(a.Field("ids"), "needs the elements to change: give one of " + choices);
            }
            if (given.Count > 1)
            {
                throw OpException.InvalidArgs(a.Field(given[1]), "give only one of " + choices + " (got " + string.Join(" and ", given) + ")");
            }

            List<Element> elements;
            string source = given[0];
            if (source == "filter")
            {
                PayloadReader filter = a.Sub("filter");
                if (filter == null || !filter.Keys.Any()) throw OpException.InvalidArgs(a.Field("filter"), "is empty; give e.g. {category:[\"Doors\"], level:\"Level 1\"}");
                ElementQuery query = ElementQuery.Compile(c.Doc, filter, c.Ctx, c.Ctx.UiDoc);
                if (query.IsLink)
                {
                    throw new OpException(ErrorCodes.LinkReadOnly, "The filter selects elements of the linked model " + query.LinkName + "; links are read-only.",
                        new Dictionary<string, object> { ["link"] = query.LinkName });
                }
                // No deadline here: a write must act on the whole match, never on a truncated scan.
                elements = query.Elements().ToList();
                if (elements.Count == 0)
                {
                    throw new OpException(ErrorCodes.NotFound, "The filter (" + string.Join("; ", query.Description) + ") matched no elements in '" + c.Doc.Title + "'.",
                        new Dictionary<string, object> { ["param"] = a.Field("filter"), ["kind"] = "element", ["value"] = string.Join("; ", query.Description), ["candidates"] = new List<object>() });
                }
            }
            else
            {
                elements = Resolve.Elements(c.Doc, a, source, c.Ctx);
            }

            elements = elements.Where(e => e != null && e.IsValidObject).ToList();
            if (!c.IsPreview && c.Doc.IsWorkshared) WorksharingGuard.EnsureEditable(c.Doc, elements.Select(e => e.Id));
            return elements;
        }

        /// <summary>The single element named by <paramref name="key"/> (with, target); several ids → the first, with a notice.</summary>
        public static List<Element> Others(ChangeContext c, PayloadReader a, string key, bool required = true)
        {
            if (!a.Has(key))
            {
                if (required) throw OpException.InvalidArgs(a.Field(key), "is required (an element id or r# handle)");
                return new List<Element>();
            }
            List<Element> elements = Resolve.Elements(c.Doc, a, key, c.Ctx).Where(e => e != null && e.IsValidObject).ToList();
            if (!c.IsPreview && c.Doc.IsWorkshared) WorksharingGuard.EnsureEditable(c.Doc, elements.Select(e => e.Id));
            return elements;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Pinned guard
        // ---------------------------------------------------------------------------------------------------------

        public static bool IsPinned(Element element)
        {
            try
            {
                return element != null && element.Pinned;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>ELEMENTS_PINNED unless allow_pinned:true. The error names the pinned ids; nothing ran.</summary>
        public static void RefusePinned(ChangeContext c, PayloadReader a, IEnumerable<Element> elements, string verb)
        {
            if (a.Bool("allow_pinned", false)) return;
            List<Element> pinned = elements.Where(IsPinned).ToList();
            if (pinned.Count == 0) return;
            throw new OpException(ElementsPinned,
                pinned.Count.ToString(CultureInfo.InvariantCulture) + " of the elements to " + verb + " are pinned (" + IdList(pinned.Select(e => e.Id)) + "); nothing was changed.",
                new Dictionary<string, object>
                {
                    ["param"] = "allow_pinned",
                    ["ids"] = pinned.Take(MaxListed).Select(e => (object)e.Id.Value).ToList(),
                    ["total"] = pinned.Count
                });
        }

        /// <summary>
        /// Runs a transform with allow_pinned elements temporarily unpinned (re-pinned afterwards), so the API never
        /// refuses a confirmed change of pinned elements.
        /// </summary>
        public static void WithUnpinned(IEnumerable<Element> elements, Action action)
        {
            List<Element> pinned = elements.Where(IsPinned).ToList();
            foreach (Element element in pinned) element.Pinned = false;
            try
            {
                action();
            }
            finally
            {
                foreach (Element element in pinned)
                {
                    try
                    {
                        if (element.IsValidObject) element.Pinned = true;
                    }
                    catch
                    {
                        // The element may have been replaced by the op; nothing to restore.
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Geometry helpers
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Center of the union bounding box of the elements (location of the first when none has a box).</summary>
        public static XYZ CenterOf(IEnumerable<Element> elements)
        {
            List<Element> list = elements.ToList();
            BoundingBoxXYZ union = Geometry.Union(list.Select(e => Geometry.BoxOf(e)).Where(b => b != null));
            if (union != null) return (union.Min + union.Max) / 2.0;
            foreach (Element element in list)
            {
                XYZ anchor = Geometry.AnchorOf(element);
                if (anchor != null) return anchor;
            }
            return XYZ.Zero;
        }

        /// <summary>A direction vector [dx,dy(,dz)] (no unit; any scale), normalized; default when absent.</summary>
        public static XYZ Direction(PayloadReader a, string key, XYZ defaultValue)
        {
            if (!a.Has(key)) return defaultValue;
            List<object> parts = a.List(key);
            if (parts.Count < 2 || parts.Count > 3) throw OpException.InvalidArgs(a.Field(key), "must be a direction [dx,dy] or [dx,dy,dz]");
            var numbers = new double[3];
            for (int index = 0; index < parts.Count; index++)
            {
                if (!PayloadReader.TryNumber(parts[index], out double value)) throw OpException.InvalidArgs(a.Field(key), "must contain numbers, got " + PayloadReader.Describe(parts[index]));
                numbers[index] = value;
            }
            var vector = new XYZ(numbers[0], numbers[1], numbers[2]);
            if (vector.GetLength() < 1e-9) throw OpException.InvalidArgs(a.Field(key), "must not be a zero vector");
            return vector.Normalize();
        }

        /// <summary>The level an element is placed on (LevelId, else common level parameters), or null.</summary>
        public static Level LevelOf(Element element)
        {
            if (element == null) return null;
            Document doc = element.Document;
            try
            {
                if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId && doc.GetElement(element.LevelId) is Level direct) return direct;
            }
            catch
            {
                // Some elements throw on LevelId.
            }
            foreach (BuiltInParameter bip in new[]
            {
                BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM, BuiltInParameter.ROOF_BASE_LEVEL_PARAM,
                BuiltInParameter.RBS_START_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM
            })
            {
                try
                {
                    Parameter parameter = element.get_Parameter(bip);
                    if (parameter == null || parameter.StorageType != StorageType.ElementId) continue;
                    if (doc.GetElement(parameter.AsElementId()) is Level level) return level;
                }
                catch
                {
                    // Try the next parameter.
                }
            }
            return null;
        }

        /// <summary>
        /// A view to run view-dependent API calls in (arrays, alignments): the owner view of view-specific elements,
        /// else the active graphical view, else a floor plan of the elements' level, else any plan or 3D view.
        /// </summary>
        public static View WorkingView(ChangeContext c, IEnumerable<Element> elements, bool planOnly = false)
        {
            List<Element> list = elements.ToList();
            foreach (Element element in list)
            {
                try
                {
                    if (element.OwnerViewId != null && element.OwnerViewId != ElementId.InvalidElementId && c.Doc.GetElement(element.OwnerViewId) is View owner) return owner;
                }
                catch
                {
                    // Not view specific.
                }
            }

            View active = null;
            try { active = c.Ctx.UiDoc?.ActiveView ?? c.Doc.ActiveView; } catch { }
            if (IsUsable(active) && (!planOnly || active is ViewPlan)) return active;

            List<View> views = new FilteredElementCollector(c.Doc).OfClass(typeof(View)).Cast<View>().Where(IsUsable).ToList();
            Level level = list.Select(LevelOf).FirstOrDefault(l => l != null);
            if (level != null)
            {
                View plan = views.OfType<ViewPlan>().FirstOrDefault(v => v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id);
                if (plan != null) return plan;
            }
            View anyPlan = views.OfType<ViewPlan>().FirstOrDefault(v => v.ViewType == ViewType.FloorPlan) ?? views.OfType<ViewPlan>().FirstOrDefault();
            if (anyPlan != null) return anyPlan;
            if (!planOnly)
            {
                View threeD = views.OfType<View3D>().FirstOrDefault();
                if (threeD != null) return threeD;
            }
            throw new OpException(ErrorCodes.NotFound, "'" + c.Doc.Title + "' has no plan view to work in; create one with edit_views first.",
                new Dictionary<string, object> { ["param"] = "view", ["kind"] = "view", ["value"] = "plan", ["candidates"] = new List<object>() });
        }

        private static bool IsUsable(View view)
        {
            if (view == null) return false;
            try
            {
                if (view.IsTemplate) return false;
                switch (view.ViewType)
                {
                    case ViewType.FloorPlan:
                    case ViewType.CeilingPlan:
                    case ViewType.EngineeringPlan:
                    case ViewType.AreaPlan:
                    case ViewType.ThreeD:
                    case ViewType.Section:
                    case ViewType.Elevation:
                    case ViewType.Detail:
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Output helpers
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>[x,y,z] in mm for output.</summary>
        public static List<object> PointMm(XYZ point)
        {
            if (point == null) return null;
            return new List<object> { Units.Mm(point.X), Units.Mm(point.Y), Units.Mm(point.Z) };
        }

        /// <summary>"304512, 304513, ... (+8)".</summary>
        public static string IdList(IEnumerable<ElementId> ids, int max = 8)
        {
            List<long> list = ids.Where(id => id != null).Select(id => id.Value).ToList();
            string head = string.Join(", ", list.Take(max).Select(id => id.ToString(CultureInfo.InvariantCulture)));
            return list.Count > max ? head + " (+" + (list.Count - max).ToString(CultureInfo.InvariantCulture) + ")" : head;
        }

        public static string Count(int n, string singular, string plural = null)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");
        }

        /// <summary>"3 walls" when all share a category, else "5 elements".</summary>
        public static string Describe(IReadOnlyCollection<Element> elements)
        {
            var labels = new HashSet<string>(elements.Select(e => Rows.CategoryLabel(e?.Category) ?? "element"), StringComparer.Ordinal);
            if (labels.Count == 1)
            {
                string label = labels.First().ToLowerInvariant();
                if (elements.Count == 1)
                {
                    if (label.EndsWith("ies", StringComparison.Ordinal)) return "1 " + label.Substring(0, label.Length - 3) + "y";
                    if (label.EndsWith("s", StringComparison.Ordinal) && !label.EndsWith("ss", StringComparison.Ordinal)) return "1 " + label.Substring(0, label.Length - 1);
                    return "1 " + label;
                }
                return elements.Count.ToString(CultureInfo.InvariantCulture) + " " + label;
            }
            return Count(elements.Count, "element");
        }

        /// <summary>A [id, reason] row for "skipped" lists.</summary>
        public static List<object> Skip(ElementId id, string reason)
        {
            return new List<object> { id?.Value, reason };
        }

        /// <summary>Adds the skipped list to data and a PARTIAL_RESULT warning naming the first reasons.</summary>
        public static void ReportSkipped(ChangeContext c, Dictionary<string, object> data, List<List<object>> skipped, string verb)
        {
            if (skipped.Count == 0) return;
            data["skipped"] = skipped.Take(50).Cast<object>().ToList();
            if (skipped.Count > 50) data["skipped_total"] = skipped.Count;
            string reasons = string.Join("; ", skipped.Select(s => Convert.ToString(s[1], CultureInfo.InvariantCulture)).Distinct().Take(3));
            c.Warn(WarningCodes.PartialResult, skipped.Count.ToString(CultureInfo.InvariantCulture) + " element(s) not " + verb + ": " + reasons,
                skipped.Select(s => s[0] is long id ? new ElementId(id) : null).Where(id => id != null).Take(MaxListed));
        }

        // ---------------------------------------------------------------------------------------------------------
        // Colors
        // ---------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, int[]> NamedColors = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = new[] { 0, 0, 0 }, ["white"] = new[] { 255, 255, 255 }, ["red"] = new[] { 255, 0, 0 },
            ["green"] = new[] { 0, 128, 0 }, ["lime"] = new[] { 0, 255, 0 }, ["blue"] = new[] { 0, 0, 255 },
            ["yellow"] = new[] { 255, 255, 0 }, ["cyan"] = new[] { 0, 255, 255 }, ["aqua"] = new[] { 0, 255, 255 },
            ["magenta"] = new[] { 255, 0, 255 }, ["fuchsia"] = new[] { 255, 0, 255 }, ["gray"] = new[] { 128, 128, 128 },
            ["grey"] = new[] { 128, 128, 128 }, ["silver"] = new[] { 192, 192, 192 }, ["maroon"] = new[] { 128, 0, 0 },
            ["olive"] = new[] { 128, 128, 0 }, ["purple"] = new[] { 128, 0, 128 }, ["teal"] = new[] { 0, 128, 128 },
            ["navy"] = new[] { 0, 0, 128 }, ["orange"] = new[] { 255, 165, 0 }, ["brown"] = new[] { 165, 42, 42 },
            ["pink"] = new[] { 255, 192, 203 }
        };

        /// <summary>#RRGGBB, #RGB, "r,g,b" or a basic CSS color name.</summary>
        public static Color ParseColor(string value, string param)
        {
            string text = (value ?? string.Empty).Trim();
            if (NamedColors.TryGetValue(text, out int[] named)) return new Color((byte)named[0], (byte)named[1], (byte)named[2]);
            if (text.StartsWith("#", StringComparison.Ordinal)) text = text.Substring(1);
            if ((text.Length == 6 || text.Length == 3) && text.All(Uri.IsHexDigit))
            {
                if (text.Length == 3) text = new string(new[] { text[0], text[0], text[1], text[1], text[2], text[2] });
                return new Color(
                    byte.Parse(text.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            string[] parts = (value ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 && v <= 255))
            {
                return new Color(
                    byte.Parse(parts[0], CultureInfo.InvariantCulture),
                    byte.Parse(parts[1], CultureInfo.InvariantCulture),
                    byte.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            throw OpException.InvalidArgs(param, "must be a color: #RRGGBB, 'r,g,b' or a name like red (got '" + value + "')");
        }

        public static string ColorText(Color color)
        {
            if (color == null || !color.IsValid) return null;
            return "#" + color.Red.ToString("X2", CultureInfo.InvariantCulture) + color.Green.ToString("X2", CultureInfo.InvariantCulture) + color.Blue.ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
