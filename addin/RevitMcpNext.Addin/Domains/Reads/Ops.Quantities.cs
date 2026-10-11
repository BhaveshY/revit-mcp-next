using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// get_quantities (SPEC Appendix A): a quantity takeoff of model elements (all, or filtered by category, level, view
    /// or an element set) summed per material (Element.GetMaterialArea/GetMaterialVolume, painted areas with paint:true),
    /// type, level, category or family. Output units: count, length m, area m², volume m³.
    /// </summary>
    internal static class QuantityOps
    {
        private sealed class Bucket
        {
            public string Name;
            public readonly HashSet<long> Elements = new HashSet<long>();
            public double LengthFt;
            public double AreaFt2;
            public double VolumeFt3;
            public double PaintFt2;
        }

        [Op("get_quantities")]
        public static OpResult Quantities(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            string by = ctx.Args.Enum("by", "material", "material", "type", "level", "category", "family");
            bool paint = ctx.Args.Bool("paint");
            Func<string, bool> materialTest = ReadKit.NameMatcher(ctx.Args.Str("material"));
            bool materialGiven = !string.IsNullOrWhiteSpace(ctx.Args.Str("material"));

            List<long> from = ReadKit.SelectorIds(ctx, doc, "from");
            var filterMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string key in new[] { "category", "level", "view" })
            {
                if (ctx.Args.TryGet(key, out object value) && value != null) filterMap[key] = value;
            }
            if (from != null) filterMap["ids"] = from.Select(id => (object)id).ToList();
            ElementQuery query = ElementQuery.Compile(doc, new PayloadReader(filterMap, ctx.Args.Path, 1.0), ctx, ctx.UiDoc);
            bool categoryGiven = ctx.Args.Strs("category").Count > 0;

            var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
            Func<Element, string> groupKey = by == "material" ? null : FindOps.GroupKey(by, doc);
            int elements = 0, scanned = 0, noMaterial = 0;
            bool truncated = false;
            foreach (Element element in query.Elements(ctx))
            {
                if (ctx.ShouldYield(scanned++))
                {
                    truncated = true;
                    break;
                }
                if (!categoryGiven && !IsModel(element)) continue;
                if (by == "material")
                {
                    bool any = AddByMaterial(element, buckets, paint, materialTest);
                    if (any) elements++;
                    else noMaterial++;
                    continue;
                }
                if (materialGiven && !HasMaterial(element, materialTest)) continue;
                string key = groupKey(element) ?? "(none)";
                Bucket bucket = Get(buckets, key);
                bucket.Elements.Add(element.Id.Value);
                bucket.LengthFt += Length(element);
                bucket.AreaFt2 += Measure(element, BuiltInParameter.HOST_AREA_COMPUTED, BuiltInParameter.ROOM_AREA);
                bucket.VolumeFt3 += Measure(element, BuiltInParameter.HOST_VOLUME_COMPUTED, BuiltInParameter.ROOM_VOLUME);
                if (paint) bucket.PaintFt2 += PaintedArea(element);
                elements++;
            }
            if (truncated || query.Truncated) ctx.Warn(WarningCodes.PartialResult, "stopped at the time budget after " + scanned.ToString("N0", CultureInfo.InvariantCulture) + " elements; totals are partial - filter by category or level");

            List<Bucket> rows = buckets.Values.OrderBy(b => b.Name, NaturalComparer.Instance).ToList();
            var cols = new List<string> { by, "count", "length_m", "area_m2", "volume_m3" };
            if (paint) cols.Add("paint_area_m2");
            var totals = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["elements"] = elements,
                ["length_m"] = Units.Number(rows.Sum(b => b.LengthFt) * Units.MmPerFoot / 1000.0, 2),
                ["area_m2"] = Units.Area(rows.Sum(b => b.AreaFt2)),
                ["volume_m3"] = Units.Volume(rows.Sum(b => b.VolumeFt3))
            };
            if (paint) totals["paint_area_m2"] = Units.Area(rows.Sum(b => b.PaintFt2));
            if (by == "material")
            {
                totals.Remove("length_m");
                if (noMaterial > 0) totals["without_material"] = noMaterial;
            }
            string summary = ReadKit.Count(rows.Count, by == "family" ? "family" : by, by == "family" ? "families" : null) + " from " + ReadKit.Count(elements, "element") +
                             (query.Description.Count > 0 ? " (" + string.Join(", ", query.Description) + ")" : string.Empty) +
                             ": " + Convert.ToString(totals["area_m2"], CultureInfo.InvariantCulture) + " m2, " + Convert.ToString(totals["volume_m3"], CultureInfo.InvariantCulture) + " m3";
            return ReadKit.Page(ctx, rows, cols, b =>
            {
                var row = new List<object>
                {
                    b.Name, b.Elements.Count,
                    by == "material" ? null : Units.Number(b.LengthFt * Units.MmPerFoot / 1000.0, 2),
                    Units.Area(b.AreaFt2), Units.Volume(b.VolumeFt3)
                };
                if (paint) row.Add(Units.Area(b.PaintFt2));
                return row;
            }, summary, new Dictionary<string, object> { ["by"] = by, ["totals"] = totals });
        }

        private static Bucket Get(Dictionary<string, Bucket> buckets, string key)
        {
            if (!buckets.TryGetValue(key, out Bucket bucket))
            {
                bucket = new Bucket { Name = key };
                buckets[key] = bucket;
            }
            return bucket;
        }

        private static bool IsModel(Element element)
        {
            try
            {
                Category category = element.Category;
                return category != null && category.CategoryType == CategoryType.Model && !(element is RevitLinkInstance) && !(element is ImportInstance);
            }
            catch
            {
                return false;
            }
        }

        private static bool AddByMaterial(Element element, Dictionary<string, Bucket> buckets, bool paint, Func<string, bool> materialTest)
        {
            Document doc = element.Document;
            bool any = false;
            ICollection<ElementId> ids;
            try { ids = element.GetMaterialIds(false); } catch { ids = new List<ElementId>(); }
            foreach (ElementId id in ids)
            {
                string name = ReadKit.NameOf(doc, id);
                if (name == null || !materialTest(name)) continue;
                double area = 0, volume = 0;
                try { area = element.GetMaterialArea(id, false); } catch { }
                try { volume = element.GetMaterialVolume(id); } catch { }
                Bucket bucket = Get(buckets, name);
                bucket.Elements.Add(element.Id.Value);
                bucket.AreaFt2 += area;
                bucket.VolumeFt3 += volume;
                any = true;
            }
            if (paint)
            {
                ICollection<ElementId> painted;
                try { painted = element.GetMaterialIds(true); } catch { painted = new List<ElementId>(); }
                foreach (ElementId id in painted)
                {
                    string name = ReadKit.NameOf(doc, id);
                    if (name == null || !materialTest(name)) continue;
                    double area = 0;
                    try { area = element.GetMaterialArea(id, true); } catch { }
                    Bucket bucket = Get(buckets, name);
                    bucket.Elements.Add(element.Id.Value);
                    bucket.PaintFt2 += area;
                    any = true;
                }
            }
            return any;
        }

        private static bool HasMaterial(Element element, Func<string, bool> materialTest)
        {
            try
            {
                return element.GetMaterialIds(false).Concat(element.GetMaterialIds(true))
                    .Any(id => materialTest(ReadKit.NameOf(element.Document, id) ?? string.Empty));
            }
            catch
            {
                return false;
            }
        }

        private static double PaintedArea(Element element)
        {
            double total = 0;
            try
            {
                foreach (ElementId id in element.GetMaterialIds(true))
                {
                    try { total += element.GetMaterialArea(id, true); } catch { }
                }
            }
            catch
            {
                // No paint.
            }
            return total;
        }

        private static double Length(Element element)
        {
            try
            {
                Parameter parameter = element.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double) return parameter.AsDouble();
                if (element.Location is LocationCurve curve && !(element is FamilyInstance)) return curve.Curve.Length;
                Parameter instanceLength = element.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM);
                if (instanceLength != null && instanceLength.HasValue && instanceLength.StorageType == StorageType.Double) return instanceLength.AsDouble();
                if (element.Location is LocationCurve framing) return framing.Curve.Length;
            }
            catch
            {
                // No length.
            }
            return 0;
        }

        private static double Measure(Element element, params BuiltInParameter[] bips)
        {
            foreach (BuiltInParameter bip in bips)
            {
                try
                {
                    Parameter parameter = element.get_Parameter(bip);
                    if (parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double) return parameter.AsDouble();
                }
                catch
                {
                    // Try the next.
                }
            }
            return 0;
        }
    }
}
