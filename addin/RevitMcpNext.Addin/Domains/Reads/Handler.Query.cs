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
        private BridgeResponseEnvelope HandleQuery(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.query.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            int limit = Math.Min(MaxQueryLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeFields(GetStringList(payload, "fields"), preset, warnings);

            string scope;
            IEnumerable<Element> elements = CreateFilteredElements(app, document, filter, warnings, out scope);
            PageResult<Element> pageResult = PageItems(elements, offset, limit, includeTotalCount);
            List<Element> page = pageResult.Items;
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["items"] = page.Select(element => BuildQueryItem(element, fields)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = pageResult.Truncated,
                ["fields"] = fields,
                ["units"] = new Dictionary<string, object>
                {
                    ["elevation"] = "mm",
                    ["length"] = "mm",
                    ["location"] = "mm",
                    ["bounds"] = "mm"
                },
                ["scope"] = scope,
                ["source"] = "revit-addin"
            };

            if (includeTotalCount && pageResult.TotalCount.HasValue) data["totalCount"] = pageResult.TotalCount.Value;
            if (pageResult.Truncated) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

            return Success(
                request,
                data,
                sw,
                warnings,
                new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = pageResult.TotalCount
                },
                generation: generation);
        }

        private static IEnumerable<Element> CreateFilteredElements(
            UIApplication app,
            Document document,
            Dictionary<string, object> filter,
            List<BridgeWarning> warnings,
            out string scope)
        {
            IReadOnlyList<string> elementIds = GetStringList(filter, "elementIds");
            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (elementIds.Count > 0 || uniqueIds.Count > 0)
            {
                scope = "elements";
                return ResolveExplicitElements(document, elementIds, uniqueIds, warnings)
                    .Where(element => MatchesPostFilters(element, filter));
            }

            bool selectionOnly = GetBool(filter, "selectionOnly", false);
            string viewId = GetString(filter, "viewId");

            if (selectionOnly)
            {
                UIDocument uidocument = app.ActiveUIDocument;
                if (uidocument == null || !IsExactDocumentIdentity(document, uidocument.Document))
                {
                    throw new TargetResolutionException(
                        "TARGET_DOCUMENT_NOT_ACTIVE",
                        "The targeted document is open but is not the UI-active Revit document required for selection-scoped operations.");
                }

                scope = "selection";
                return uidocument.Selection.GetElementIds()
                    .Select(id => document.GetElement(id))
                    .Where(element => element != null)
                    .Where(element => MatchesPostFilters(element, filter));
            }

            FilteredElementCollector collector;
            if (!string.IsNullOrWhiteSpace(viewId))
            {
                ElementId parsedViewId = CreateElementId(viewId);
                collector = new FilteredElementCollector(document, parsedViewId);
                scope = "view:" + viewId;
            }
            else
            {
                collector = new FilteredElementCollector(document);
                scope = "activeDocument";
            }

            collector.WhereElementIsNotElementType();
            bool nativeCategoryFilter = TryApplyCategoryFilter(collector, GetStringList(filter, "categories"), warnings);
            bool nativeClassFilter = TryApplyClassFilter(collector, GetStringList(filter, "classes"), warnings);

            // Enumerate the collector lazily so Skip/Take and scan limits stop early instead of
            // materializing every element in the document before paging.
            IEnumerable<Element> elements = collector;
            return (!nativeCategoryFilter || !nativeClassFilter)
                ? elements.Where(element => MatchesPostFilters(element, filter))
                : elements.Where(element => MatchesSecondaryPostFilters(element, filter));
        }

        private static IEnumerable<Element> ResolveExplicitElements(
            Document document,
            IReadOnlyList<string> elementIds,
            IReadOnlyList<string> uniqueIds,
            List<BridgeWarning> warnings)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolved = new List<Element>();

            foreach (string elementId in elementIds)
            {
                Element element = null;
                try
                {
                    element = document.GetElement(CreateElementId(elementId));
                }
                catch
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "INVALID_ELEMENT_ID_FILTER",
                        Message = "filter.elementIds contains a non-numeric element ID; it was ignored."
                    });
                }

                if (element == null) continue;
                string key = ToElementIdString(element.Id);
                if (seen.Add(key)) resolved.Add(element);
            }

            foreach (string uniqueId in uniqueIds)
            {
                Element element = null;
                try
                {
                    element = document.GetElement(uniqueId);
                }
                catch
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "INVALID_UNIQUE_ID_FILTER",
                        Message = "filter.uniqueIds contains an invalid UniqueId; it was ignored."
                    });
                }

                if (element == null) continue;
                string key = ToElementIdString(element.Id);
                if (seen.Add(key)) resolved.Add(element);
            }

            return resolved;
        }

        private static bool TryApplyCategoryFilter(
            FilteredElementCollector collector,
            IReadOnlyList<string> categories,
            List<BridgeWarning> warnings)
        {
            if (categories.Count == 0) return true;

            var builtInCategories = new List<BuiltInCategory>();
            foreach (string category in categories)
            {
                if (!TryParseBuiltInCategory(category, out BuiltInCategory builtInCategory))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "CATEGORY_POST_FILTER",
                        Message = "Category '" + category + "' is not a BuiltInCategory name; applying a slower post-filter."
                    });
                    return false;
                }

                builtInCategories.Add(builtInCategory);
            }

            collector.WherePasses(new ElementMulticategoryFilter(builtInCategories));
            return true;
        }

        private static bool TryApplyClassFilter(
            FilteredElementCollector collector,
            IReadOnlyList<string> classes,
            List<BridgeWarning> warnings)
        {
            if (classes.Count == 0) return true;

            var elementTypes = new List<Type>();
            foreach (string className in classes)
            {
                Type type = ResolveElementType(className);
                if (type == null)
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "CLASS_POST_FILTER",
                        Message = "Class '" + className + "' is not a recognized Autodesk.Revit.DB element class; applying a slower post-filter."
                    });
                    return false;
                }

                elementTypes.Add(type);
            }

            collector.WherePasses(new ElementMulticlassFilter(elementTypes));
            return true;
        }

        private static bool MatchesPostFilters(Element element, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> categories = GetStringList(filter, "categories");
            if (categories.Count > 0 && !MatchesCategory(element, categories)) return false;

            IReadOnlyList<string> classes = GetStringList(filter, "classes");
            if (classes.Count > 0 && !MatchesClass(element, classes)) return false;

            return MatchesSecondaryPostFilters(element, filter);
        }

        private static bool MatchesSecondaryPostFilters(Element element, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> levelIds = GetStringList(filter, "levelIds");
            if (levelIds.Count > 0 && !levelIds.Contains(ToElementIdString(GetLevelId(element)), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> worksetIds = GetStringList(filter, "worksetIds");
            if (worksetIds.Count > 0 && !worksetIds.Contains(ToWorksetIdString(element.WorksetId), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> designOptionIds = GetStringList(filter, "designOptionIds");
            if (designOptionIds.Count > 0 && !designOptionIds.Contains(ToElementIdString(GetDesignOptionId(element)), StringComparer.OrdinalIgnoreCase)) return false;

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

        private static bool MatchesCategory(Element element, IReadOnlyList<string> categories)
        {
            string categoryName = element.Category?.Name ?? string.Empty;
            string builtInName = string.Empty;
            try
            {
                if (element.Category != null)
                {
                    builtInName = ((BuiltInCategory)GetElementIdValue(element.Category.Id)).ToString();
                }
            }
            catch
            {
                builtInName = string.Empty;
            }

            return categories.Any(category =>
                string.Equals(category, categoryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, builtInName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals("OST_" + category.Replace(" ", string.Empty), builtInName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesClass(Element element, IReadOnlyList<string> classes)
        {
            string className = element.GetType().Name;
            return classes.Any(value => string.Equals(value, className, StringComparison.OrdinalIgnoreCase));
        }

        private static int CountCollectorElements(FilteredElementCollector collector)
        {
            using (collector)
            {
                return collector.GetElementCount();
            }
        }

        private static bool IsModelElement(Element element)
        {
            try
            {
                return element.Category != null && element.Category.CategoryType == CategoryType.Model;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, object> BuildQueryItem(Element element, IReadOnlyList<string> fields)
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
                    case "ownerViewId": item["ownerViewId"] = ToElementIdString(element.OwnerViewId); break;
                    case "viewSpecific": item["viewSpecific"] = element.ViewSpecific; break;
                    case "uniqueId":
                        item["uniqueId"] = element.UniqueId;
                        break;
                    case "category":
                        item["category"] = element.Category?.Name;
                        break;
                    case "class":
                        item["class"] = element.GetType().Name;
                        break;
                    case "name":
                        item["name"] = SafeElementName(element);
                        break;
                    case "typeId":
                        item["typeId"] = ToElementIdString(element.GetTypeId());
                        break;
                    case "levelId":
                        ElementId levelId = GetLevelId(element);
                        if (IsValidElementId(levelId)) item["levelId"] = ToElementIdString(levelId);
                        break;
                    case "location":
                        Dictionary<string, object> location = LocationSnapshot(element);
                        if (location != null) item["location"] = location;
                        break;
                    case "bounds":
                        Dictionary<string, object> bounds = BoundsSnapshot(element);
                        if (bounds != null) item["bounds"] = bounds;
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

        private static ElementId GetLevelId(Element element)
        {
            object value = element.GetType().GetProperty("LevelId")?.GetValue(element, null);
            if (value is ElementId reflectedLevelId && IsValidElementId(reflectedLevelId)) return reflectedLevelId;

            foreach (BuiltInParameter builtInParameter in new[]
            {
                BuiltInParameter.LEVEL_PARAM,
                BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM
            })
            {
                Parameter parameter = element.get_Parameter(builtInParameter);
                ElementId id = parameter?.AsElementId();
                if (IsValidElementId(id)) return id;
            }

            return ElementId.InvalidElementId;
        }

        private static ElementId GetDesignOptionId(Element element)
        {
            object value = element.GetType().GetProperty("DesignOption")?.GetValue(element, null);
            Element designOption = value as Element;
            return designOption?.Id ?? ElementId.InvalidElementId;
        }

        private static bool IsBuildingStory(Level level)
        {
            object reflected = typeof(Level).GetProperty("IsBuildingStory")?.GetValue(level, null);
            if (reflected is bool value) return value;

            if (Enum.IsDefined(typeof(BuiltInParameter), "LEVEL_IS_BUILDING_STORY"))
            {
                var builtInParameter = (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), "LEVEL_IS_BUILDING_STORY");
                Parameter parameter = level.get_Parameter(builtInParameter);
                return parameter != null && parameter.AsInteger() != 0;
            }

            return false;
        }

        private static bool ParameterEquals(Element element, string parameterName, object expected)
        {
            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null) return false;

            object actual = ParameterValue(parameter);
            if (actual == null) return expected == null;

            if (expected is bool expectedBool)
            {
                if (actual is int actualInt) return (actualInt != 0) == expectedBool;
                if (bool.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), out bool actualBool)) return actualBool == expectedBool;
            }

            if (IsNumeric(expected) && IsNumeric(actual))
            {
                return Math.Abs(Convert.ToDouble(actual, CultureInfo.InvariantCulture) - Convert.ToDouble(expected, CultureInfo.InvariantCulture)) < 0.000001;
            }

            return string.Equals(
                Convert.ToString(actual, CultureInfo.InvariantCulture),
                Convert.ToString(expected, CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
        }

        private static object ParameterValue(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return parameter.AsDouble();
                case StorageType.Integer:
                    return parameter.AsInteger();
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.ElementId:
                    return ToElementIdString(parameter.AsElementId());
                default:
                    return parameter.AsValueString();
            }
        }

        private static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                   value is int || value is uint || value is long || value is ulong ||
                   value is float || value is double || value is decimal;
        }

        private static bool ContainsIgnoreCase(string value, string needle)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   !string.IsNullOrWhiteSpace(needle) &&
                   value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
