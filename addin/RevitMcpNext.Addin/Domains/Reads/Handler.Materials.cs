using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private LegacyResponse HandleGetMaterialQuantities(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_material_quantities.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            IReadOnlyList<string> categoryFilters = GetStringList(payload, "categoryFilters");
            if (categoryFilters.Count > 0 && GetStringList(filter, "categories").Count == 0)
            {
                filter["categories"] = categoryFilters.ToArray();
            }

            if (GetBool(payload, "selectedElementsOnly", false))
            {
                filter["selectionOnly"] = true;
            }

            int limit = Math.Min(MaxMaterialLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includePaint = GetBool(payload, "includePaint", false);
            int maxElementsScanned = Math.Min(MaxMaterialScanLimit, Math.Max(1, GetInt(payload, "maxElementsScanned") ?? 20000));
            string materialNameContains = GetString(payload, "materialNameContains");

            var collectorSw = Stopwatch.StartNew();
            string scope;
            IEnumerable<Element> scopedElements = CreateFilteredElements(app, document, filter, warnings, out scope);
            var accumulators = new Dictionary<string, MaterialQuantityAccumulator>(StringComparer.OrdinalIgnoreCase);
            int elementsScanned = 0;
            int elementsWithMaterials = 0;
            bool scanTruncated = false;

            foreach (Element element in scopedElements)
            {
                if (elementsScanned >= maxElementsScanned)
                {
                    scanTruncated = true;
                    break;
                }

                elementsScanned++;
                if (AccumulateMaterialQuantities(document, element, includePaint, accumulators, warnings))
                {
                    elementsWithMaterials++;
                }
            }

            if (scanTruncated)
            {
                warnings.Add(new LegacyWarning
                {
                    Code = "MATERIAL_SCAN_TRUNCATED",
                    Message = "Material quantities were computed from the first " + elementsScanned.ToString(CultureInfo.InvariantCulture) + " scoped elements. Increase maxElementsScanned for a deeper scan."
                });
            }

            List<MaterialQuantityAccumulator> materialized = accumulators.Values
                .Where(item => string.IsNullOrWhiteSpace(materialNameContains) || item.MaterialName.IndexOf(materialNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(item => item.Volume)
                .ThenByDescending(item => item.Area)
                .ThenBy(item => item.MaterialName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<MaterialQuantityAccumulator> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["scope"] = scope,
                ["items"] = page.Select(BuildMaterialQuantityItem).ToArray(),
                ["elementsScanned"] = elementsScanned,
                ["elementsWithMaterials"] = elementsWithMaterials,
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < materialized.Count || scanTruncated,
                ["units"] = new Dictionary<string, object>
                {
                    ["area"] = "m2",
                    ["volume"] = "m3"
                },
                ["source"] = "revit-addin"
            };

            if (includeTotalCount) data["totalCount"] = materialized.Count;
            if (offset + page.Count < materialized.Count) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

            return Success(
                request,
                data,
                sw,
                warnings,
                new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = includeTotalCount ? materialized.Count : (int?)null
                },
                generation: generation);
        }

        private static bool AccumulateMaterialQuantities(
            Document document,
            Element element,
            bool includePaint,
            Dictionary<string, MaterialQuantityAccumulator> accumulators,
            List<LegacyWarning> warnings)
        {
            bool found = AccumulateMaterialIds(document, element, usePaintMaterial: false, accumulators, warnings);
            if (includePaint)
            {
                found = AccumulateMaterialIds(document, element, usePaintMaterial: true, accumulators, warnings) || found;
            }

            return found;
        }

        private static bool AccumulateMaterialIds(
            Document document,
            Element element,
            bool usePaintMaterial,
            Dictionary<string, MaterialQuantityAccumulator> accumulators,
            List<LegacyWarning> warnings)
        {
            ICollection<ElementId> materialIds;
            try
            {
                materialIds = element.GetMaterialIds(usePaintMaterial);
            }
            catch (Exception ex)
            {
                AddWarningOnce(
                    warnings,
                    "MATERIAL_IDS_UNAVAILABLE",
                    "One or more elements did not expose material IDs: " + ex.Message);
                return false;
            }

            if (materialIds == null || materialIds.Count == 0) return false;

            bool found = false;
            foreach (ElementId materialId in materialIds)
            {
                if (!IsValidElementId(materialId)) continue;
                Material material = document.GetElement(materialId) as Material;
                string materialIdString = ToElementIdString(materialId);
                MaterialQuantityAccumulator accumulator;
                if (!accumulators.TryGetValue(materialIdString, out accumulator))
                {
                    accumulator = new MaterialQuantityAccumulator(materialIdString, material);
                    accumulators[materialIdString] = accumulator;
                }

                string elementIdString = ToElementIdString(element.Id);
                accumulator.ElementIds.Add(elementIdString);
                accumulator.IncrementCategory(element.Category?.Name ?? "(none)");
                if (usePaintMaterial) accumulator.HasPaint = true;
                else accumulator.HasRegular = true;

                try
                {
                    accumulator.Area += element.GetMaterialArea(materialId, usePaintMaterial);
                }
                catch (Exception ex)
                {
                    AddWarningOnce(
                        warnings,
                        "MATERIAL_AREA_UNAVAILABLE",
                        "One or more material areas could not be read: " + ex.Message);
                }

                if (!usePaintMaterial)
                {
                    try
                    {
                        accumulator.Volume += element.GetMaterialVolume(materialId);
                    }
                    catch (Exception ex)
                    {
                        AddWarningOnce(
                            warnings,
                            "MATERIAL_VOLUME_UNAVAILABLE",
                            "One or more material volumes could not be read: " + ex.Message);
                    }
                }

                found = true;
            }

            return found;
        }

        private static Dictionary<string, object> BuildMaterialQuantityItem(MaterialQuantityAccumulator item)
        {
            var result = new Dictionary<string, object>
            {
                ["materialId"] = item.MaterialId,
                ["materialName"] = item.MaterialName,
                ["elementCount"] = item.ElementIds.Count,
                ["area"] = AreaValue(item.Area),
                ["volume"] = VolumeValue(item.Volume),
                ["source"] = item.HasRegular && item.HasPaint ? "mixed" : item.HasPaint ? "paint" : "regular",
                ["categories"] = item.Categories
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .Select(pair => new Dictionary<string, object>
                    {
                        ["name"] = pair.Key,
                        ["count"] = pair.Value
                    })
                    .ToArray()
            };

            if (!string.IsNullOrWhiteSpace(item.MaterialClass)) result["materialClass"] = item.MaterialClass;
            return result;
        }

        private sealed class MaterialQuantityAccumulator
        {
            public MaterialQuantityAccumulator(string materialId, Material material)
            {
                MaterialId = materialId;
                string materialName = SafeElementName(material);
                MaterialName = string.IsNullOrWhiteSpace(materialName) ? materialId : materialName;
                try
                {
                    MaterialClass = material?.MaterialClass;
                }
                catch
                {
                    MaterialClass = null;
                }
            }

            public string MaterialId { get; }
            public string MaterialName { get; }
            public string MaterialClass { get; }
            public double Area { get; set; }
            public double Volume { get; set; }
            public bool HasRegular { get; set; }
            public bool HasPaint { get; set; }
            public HashSet<string> ElementIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> Categories { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void IncrementCategory(string category)
            {
                string key = string.IsNullOrWhiteSpace(category) ? "(none)" : category;
                Categories[key] = Categories.TryGetValue(key, out int count) ? count + 1 : 1;
            }
        }
    }
}
