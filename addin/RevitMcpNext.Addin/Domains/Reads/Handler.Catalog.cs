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
        private LegacyResponse HandleCatalog(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.catalog.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            string kind = GetString(payload, "kind");
            if (!IsSupportedCatalogKind(kind))
            {
                return Failure(request, "INVALID_CATALOG_KIND", "Unsupported catalog kind: " + (kind ?? "(missing)"), sw);
            }

            string forElementId = GetString(filter, "forElementId");
            if (!string.IsNullOrWhiteSpace(forElementId) && !string.Equals(kind, "elementTypes", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(request, "INVALID_CATALOG_FILTER", "filter.forElementId is only valid with kind=elementTypes.", sw);
            }

            int limit = Math.Min(MaxCatalogLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeCatalogFields(GetStringList(payload, "fields"), preset, warnings);

            Element targetElement = null;
            HashSet<string> validTypeIds = null;
            Dictionary<string, object> target = null;
            if (!string.IsNullOrWhiteSpace(forElementId))
            {
                targetElement = ResolveElement(document, forElementId);
                if (targetElement == null)
                {
                    return Failure(request, "ELEMENT_NOT_FOUND", "Element " + forElementId + " was not found.", sw);
                }

                if (targetElement is ElementType)
                {
                    return Failure(request, "INVALID_CATALOG_TARGET", "filter.forElementId must reference a model element, not an element type.", sw);
                }

                validTypeIds = GetValidTypeIdSet(targetElement, warnings);
                target = BuildCatalogTarget(document, targetElement, validTypeIds);
            }

            List<Element> materialized = CreateCatalogElements(document, kind, filter, warnings, targetElement, validTypeIds)
                .OrderBy(element => element.Category?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetFamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(SafeElementName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(element => element.GetType().Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(element => GetElementIdValue(element.Id))
                .ToList();

            int totalCount = materialized.Count;
            List<Element> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["items"] = page.Select(element => BuildCatalogItem(document, element, fields, targetElement, validTypeIds)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["scope"] = string.IsNullOrWhiteSpace(forElementId) ? "activeDocument" : "typeChange:" + forElementId,
                ["source"] = "revit-addin",
                ["units"] = new Dictionary<string, object>()
            };

            if (target != null) data["target"] = target;
            if (includeTotalCount) data["totalCount"] = totalCount;
            if (offset + page.Count < totalCount) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

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
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private static bool IsSupportedCatalogKind(string kind)
        {
            return string.Equals(kind, "elementTypes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "familySymbols", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "titleBlocks", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "viewFamilyTypes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "textNoteTypes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "dimensionTypes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(kind, "tagTypes", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<Element> CreateCatalogElements(
            Document document,
            string kind,
            Dictionary<string, object> filter,
            List<LegacyWarning> warnings,
            Element targetElement,
            HashSet<string> validTypeIds)
        {
            if (string.Equals(kind, "tagTypes", StringComparison.OrdinalIgnoreCase))
            {
                return new FilteredElementCollector(document)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<Element>()
                    .Where(IsTagFamilySymbol)
                    .Where(element => MatchesCatalogFilters(element, filter));
            }

            FilteredElementCollector collector = new FilteredElementCollector(document);
            if (string.Equals(kind, "familySymbols", StringComparison.OrdinalIgnoreCase))
            {
                collector.OfClass(typeof(FamilySymbol));
            }
            else if (string.Equals(kind, "titleBlocks", StringComparison.OrdinalIgnoreCase))
            {
                collector.OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks);
            }
            else if (string.Equals(kind, "viewFamilyTypes", StringComparison.OrdinalIgnoreCase))
            {
                collector.OfClass(typeof(ViewFamilyType));
            }
            else if (string.Equals(kind, "textNoteTypes", StringComparison.OrdinalIgnoreCase))
            {
                collector.OfClass(typeof(TextNoteType));
            }
            else if (string.Equals(kind, "dimensionTypes", StringComparison.OrdinalIgnoreCase))
            {
                collector.OfClass(typeof(DimensionType));
            }
            else
            {
                collector.WhereElementIsElementType();
            }

            IEnumerable<Element> elements = collector;
            if (targetElement != null)
            {
                if (validTypeIds == null || validTypeIds.Count == 0)
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "NO_VALID_TYPES_REPORTED",
                        Message = "Revit did not report valid replacement types for element " + ToElementIdString(targetElement.Id) + "."
                    });
                    elements = Enumerable.Empty<Element>();
                }
                else
                {
                    elements = elements.Where(element => validTypeIds.Contains(ToElementIdString(element.Id)));
                }
            }

            return elements.Where(element => MatchesCatalogFilters(element, filter));
        }

        private static bool MatchesCatalogFilters(Element element, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> categories = GetStringList(filter, "categories");
            if (categories.Count > 0 && !MatchesCategory(element, categories)) return false;

            IReadOnlyList<string> classes = GetStringList(filter, "classes");
            if (classes.Count > 0 && !MatchesClass(element, classes)) return false;

            string familyName = GetString(filter, "familyName");
            if (!string.IsNullOrWhiteSpace(familyName) &&
                !string.Equals(GetFamilyName(element), familyName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string familyNameContains = GetString(filter, "familyNameContains");
            if (!string.IsNullOrWhiteSpace(familyNameContains) &&
                (GetFamilyName(element) ?? string.Empty).IndexOf(familyNameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (SafeElementName(element) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            IReadOnlyList<string> viewFamilies = GetStringList(filter, "viewFamily");
            if (viewFamilies.Count > 0 && !viewFamilies.Contains(GetViewFamily(element), StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            Dictionary<string, object> parameterEquals = GetDictionary(filter, "parameterEquals");
            if (parameterEquals != null)
            {
                foreach (KeyValuePair<string, object> expected in parameterEquals)
                {
                    if (!ParameterEquals(element, expected.Key, expected.Value)) return false;
                }
            }

            return true;
        }

        private static Dictionary<string, object> BuildCatalogTarget(
            Document document,
            Element element,
            HashSet<string> validTypeIds)
        {
            ElementId currentTypeId = element.GetTypeId();
            bool hasCurrentType = IsValidElementId(currentTypeId);
            var target = new Dictionary<string, object>
            {
                ["elementId"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element),
                ["pinned"] = element.Pinned,
                ["canChangeType"] = !element.Pinned && validTypeIds != null && validTypeIds.Count > 0,
                ["validTypeCount"] = validTypeIds?.Count ?? 0
            };

            if (element.Category != null) target["category"] = element.Category.Name;
            if (hasCurrentType)
            {
                target["currentTypeId"] = ToElementIdString(currentTypeId);
                ElementType currentType = document.GetElement(currentTypeId) as ElementType;
                if (currentType != null) target["currentTypeName"] = SafeElementName(currentType);
            }

            return target;
        }

        private static Dictionary<string, object> BuildCatalogItem(
            Document document,
            Element element,
            IReadOnlyList<string> fields,
            Element targetElement,
            HashSet<string> validTypeIds)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(element.Id)
            };

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "uniqueId":
                        item["uniqueId"] = element.UniqueId;
                        break;
                    case "class":
                        item["class"] = element.GetType().Name;
                        break;
                    case "category":
                        if (element.Category != null) item["category"] = element.Category.Name;
                        break;
                    case "builtInCategory":
                        string builtInCategory = GetBuiltInCategoryName(element);
                        if (!string.IsNullOrWhiteSpace(builtInCategory)) item["builtInCategory"] = builtInCategory;
                        break;
                    case "name":
                        item["name"] = SafeElementName(element);
                        break;
                    case "familyName":
                        string familyName = GetFamilyName(element);
                        if (!string.IsNullOrWhiteSpace(familyName)) item["familyName"] = familyName;
                        break;
                    case "familyId":
                        string familyId = GetFamilyIdString(element);
                        if (!string.IsNullOrWhiteSpace(familyId)) item["familyId"] = familyId;
                        break;
                    case "isCurrentType":
                        item["isCurrentType"] = IsCurrentType(targetElement, element);
                        break;
                    case "validForTarget":
                        item["validForTarget"] = validTypeIds != null && validTypeIds.Contains(ToElementIdString(element.Id));
                        break;
                    case "isActive":
                        if (element is FamilySymbol symbol) item["isActive"] = symbol.IsActive;
                        break;
                    case "placementType":
                        string placementType = GetPlacementType(element);
                        if (!string.IsNullOrWhiteSpace(placementType)) item["placementType"] = placementType;
                        break;
                    case "dimensionStyle":
                        if (element is DimensionType dimensionType) item["dimensionStyle"] = dimensionType.StyleType.ToString();
                        break;
                    case "viewFamily":
                        string viewFamily = GetViewFamily(element);
                        if (!string.IsNullOrWhiteSpace(viewFamily)) item["viewFamily"] = viewFamily;
                        break;
                    default:
                        if (field.StartsWith("param:", StringComparison.OrdinalIgnoreCase))
                        {
                            string parameterName = field.Substring("param:".Length);
                            Parameter parameter = element.LookupParameter(parameterName);
                            if (parameter != null)
                            {
                                Dictionary<string, object> extras = GetOrCreateFields(item);
                                extras[parameterName] = ParameterValue(parameter);
                            }
                        }
                        break;
                }
            }

            return item;
        }

        private static HashSet<string> GetValidTypeIdSet(Element element, List<LegacyWarning> warnings)
        {
            try
            {
                ICollection<ElementId> validTypeIds = element.GetValidTypes();
                return new HashSet<string>(
                    validTypeIds?.Where(IsValidElementId).Select(ToElementIdString) ?? Enumerable.Empty<string>(),
                    StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                warnings.Add(new LegacyWarning
                {
                    Code = "VALID_TYPES_UNAVAILABLE",
                    Message = "Unable to read valid replacement types for element " + ToElementIdString(element.Id) + ": " + ex.Message
                });
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private static bool IsCurrentType(Element targetElement, Element candidateType)
        {
            if (targetElement == null || candidateType == null) return false;
            ElementId currentTypeId = targetElement.GetTypeId();
            return IsValidElementId(currentTypeId) &&
                   string.Equals(ToElementIdString(currentTypeId), ToElementIdString(candidateType.Id), StringComparison.Ordinal);
        }

        private static string GetBuiltInCategoryName(Element element)
        {
            try
            {
                if (element?.Category == null) return string.Empty;
                return ((BuiltInCategory)GetElementIdValue(element.Category.Id)).ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetFamilyName(Element element)
        {
            if (element is FamilySymbol symbol)
            {
                return symbol.Family?.Name ?? symbol.FamilyName;
            }

            if (element is ViewFamilyType viewFamilyType)
            {
                return viewFamilyType.ViewFamily.ToString();
            }

            if (element is ElementType elementType)
            {
                return elementType.FamilyName;
            }

            return string.Empty;
        }

        private static string GetFamilyIdString(Element element)
        {
            FamilySymbol symbol = element as FamilySymbol;
            if (symbol?.Family == null || !IsValidElementId(symbol.Family.Id)) return string.Empty;
            return ToElementIdString(symbol.Family.Id);
        }

        private static bool IsTagFamilySymbol(Element element)
        {
            if (!(element is FamilySymbol)) return false;

            string builtInCategory = GetBuiltInCategoryName(element);
            string categoryName = element.Category?.Name ?? string.Empty;
            return builtInCategory.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   categoryName.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRoomTagTypeElement(Element element)
        {
            if (element == null) return false;

            string builtInCategory = GetBuiltInCategoryName(element);
            string categoryName = element.Category?.Name ?? string.Empty;
            return string.Equals(builtInCategory, "OST_RoomTags", StringComparison.OrdinalIgnoreCase) ||
                   categoryName.IndexOf("Room Tag", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMaterialTagFamilySymbol(Element element)
        {
            string builtInCategory = GetBuiltInCategoryName(element);
            string categoryName = element.Category?.Name ?? string.Empty;
            return builtInCategory.IndexOf("MaterialTag", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   categoryName.IndexOf("Material Tag", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsBuiltInCategory(Element element, params BuiltInCategory[] categories)
        {
            string builtInCategory = GetBuiltInCategoryName(element);
            return categories != null && categories.Any(category =>
                string.Equals(category.ToString(), builtInCategory, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetViewFamily(Element element)
        {
            ViewFamilyType viewFamilyType = element as ViewFamilyType;
            return viewFamilyType?.ViewFamily.ToString() ?? string.Empty;
        }
    }
}
