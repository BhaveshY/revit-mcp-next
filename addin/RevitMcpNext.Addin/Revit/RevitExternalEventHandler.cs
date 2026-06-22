using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class RevitExternalEventHandler : IExternalEventHandler
    {
        private const string AddinVersion = "0.1.0";
        private const int MaxItemsPerExternalEvent = 16;
        private const int MaxQueryLimit = 500;
        private readonly RevitRequestQueue _queue;
        private readonly TransactionService _transactions;

        public RevitExternalEventHandler(RevitRequestQueue queue, TransactionService transactions)
        {
            _queue = queue;
            _transactions = transactions;
        }

        public void Execute(UIApplication app)
        {
            int processed = 0;
            while (processed < MaxItemsPerExternalEvent && _queue.TryDequeue(out RevitRequestQueue.WorkItem item))
            {
                processed++;
                item.TrySetResult(Handle(app, item.Envelope));
            }
        }

        public string GetName()
        {
            return "Revit MCP Next External Event Handler";
        }

        private BridgeResponseEnvelope Handle(UIApplication app, BridgeRequestEnvelope request)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return _transactions.Read(() =>
                {
                    switch (request.Operation)
                    {
                        case "status":
                            return Success(request, BuildStatus(app), sw);
                        case "list_documents":
                            return Success(request, BuildDocumentList(app), sw);
                        case "get_levels":
                            return HandleGetLevels(app, request, sw);
                        case "query":
                            return HandleQuery(app, request, sw);
                        default:
                            return Failure(request, "UNSUPPORTED_OPERATION", "Unsupported Revit MCP operation: " + request.Operation, sw);
                    }
                });
            }
            catch (Exception ex)
            {
                return Failure(request, "REVIT_COMMAND_FAILED", ex.Message, sw);
            }
        }

        private static BridgeResponseEnvelope HandleGetLevels(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_levels.", sw);
            }

            var collectorSw = Stopwatch.StartNew();
            var levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .Select(BuildLevelSummary)
                .ToArray();
            collectorSw.Stop();

            return Success(
                request,
                levels,
                sw,
                metrics: new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = levels.Length,
                    TotalCount = levels.Length
                });
        }

        private static BridgeResponseEnvelope HandleQuery(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.query.", sw);
            }

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
            List<Element> materialized = elements.ToList();
            int totalCount = materialized.Count;
            List<Element> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["items"] = page.Select(element => BuildQueryItem(element, fields)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["units"] = new Dictionary<string, object>
                {
                    ["elevation"] = "mm",
                    ["length"] = "mm"
                },
                ["scope"] = scope,
                ["source"] = "revit-addin"
            };

            if (includeTotalCount) data["totalCount"] = totalCount;
            if (offset + page.Count < totalCount) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

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
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                });
        }

        private static Dictionary<string, object> BuildStatus(UIApplication app)
        {
            UIDocument activeUiDocument = app.ActiveUIDocument;
            Document activeDocument = activeUiDocument?.Document;
            var data = new Dictionary<string, object>
            {
                ["connected"] = true,
                ["brokerVersion"] = "unknown",
                ["addinVersion"] = AddinVersion,
                ["protocolVersion"] = BridgeProtocol.Version,
                ["revit"] = new Dictionary<string, object>
                {
                    ["version"] = app.Application.VersionNumber,
                    ["build"] = app.Application.VersionBuild,
                    ["processId"] = Process.GetCurrentProcess().Id
                },
                ["selection"] = new Dictionary<string, object>
                {
                    ["count"] = activeUiDocument?.Selection?.GetElementIds()?.Count ?? 0
                },
                ["capabilities"] = new[] { "status", "list_documents", "get_levels", "query" },
                ["warnings"] = Array.Empty<object>()
            };

            if (activeDocument != null)
            {
                data["activeDocument"] = BuildDocumentSummary(activeDocument, activeDocument);
            }

            return data;
        }

        private static object[] BuildDocumentList(UIApplication app)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            return EnumerateDocuments(app)
                .Select(document => BuildDocumentSummary(document, activeDocument))
                .ToArray();
        }

        private static IEnumerable<Element> CreateFilteredElements(
            UIApplication app,
            Document document,
            Dictionary<string, object> filter,
            List<BridgeWarning> warnings,
            out string scope)
        {
            bool selectionOnly = GetBool(filter, "selectionOnly", false);
            string viewId = GetString(filter, "viewId");

            if (selectionOnly)
            {
                UIDocument uidocument = app.ActiveUIDocument;
                if (uidocument == null || !ReferenceEquals(uidocument.Document, document))
                {
                    scope = "selection";
                    warnings.Add(new BridgeWarning { Code = "SELECTION_UNAVAILABLE", Message = "Selection is only available on the active document." });
                    return Enumerable.Empty<Element>();
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

            IEnumerable<Element> elements = collector.ToElements();
            return (!nativeCategoryFilter || !nativeClassFilter)
                ? elements.Where(element => MatchesPostFilters(element, filter))
                : elements.Where(element => MatchesSecondaryPostFilters(element, filter));
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

        private static Dictionary<string, object> GetOrCreateFields(Dictionary<string, object> item)
        {
            if (!item.TryGetValue("fields", out object existing) || !(existing is Dictionary<string, object> fields))
            {
                fields = new Dictionary<string, object>();
                item["fields"] = fields;
            }

            return fields;
        }

        private static string[] NormalizeFields(IReadOnlyList<string> requested, string preset, List<BridgeWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "schedule":
                    defaults = new[] { "id", "category", "name", "typeId", "levelId" };
                    break;
                case "geometrySummary":
                    warnings.Add(new BridgeWarning
                    {
                        Code = "GEOMETRY_SUMMARY_NOT_READY",
                        Message = "geometrySummary currently returns summary fields; geometry extraction will be added behind explicit budgets."
                    });
                    defaults = SummaryFields();
                    break;
                default:
                    defaults = SummaryFields();
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedField(field))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "UNSUPPORTED_FIELD",
                        Message = "Field '" + field + "' is not supported by the current query projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static string[] SummaryFields()
        {
            return new[] { "id", "uniqueId", "category", "class", "name", "typeId", "levelId" };
        }

        private static bool IsSupportedField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "category":
                case "class":
                case "name":
                case "typeId":
                case "levelId":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }

        private static Dictionary<string, object> BuildDocumentSummary(Document document, Document activeDocument)
        {
            var summary = new Dictionary<string, object>
            {
                ["documentId"] = GetDocumentId(document),
                ["title"] = document.Title,
                ["fingerprint"] = ComputeDocumentFingerprint(document),
                ["isActive"] = ReferenceEquals(document, activeDocument),
                ["isWorkshared"] = document.IsWorkshared,
                ["isModified"] = document.IsModified,
                ["generation"] = 0
            };

            if (!string.IsNullOrWhiteSpace(document.PathName)) summary["path"] = document.PathName;

            View activeView = SafeActiveView(document);
            if (activeView != null)
            {
                summary["activeView"] = BuildViewSummary(activeView);
            }

            return summary;
        }

        private static Dictionary<string, object> BuildViewSummary(View view)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(view.Id),
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
                ["isGraphical"] = !view.IsTemplate && view.CanBePrinted
            };

            if (view.Scale > 0) summary["scale"] = view.Scale;
            return summary;
        }

        private static Dictionary<string, object> BuildLevelSummary(Level level)
        {
            return new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(level.Id),
                ["uniqueId"] = level.UniqueId,
                ["name"] = level.Name,
                ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters), "mm", "metric"),
                ["isBuildingStory"] = IsBuildingStory(level)
            };
        }

        private static Dictionary<string, object> UnitValue(double value, string unit, string system)
        {
            return new Dictionary<string, object>
            {
                ["value"] = Math.Round(value, 6),
                ["unit"] = unit,
                ["system"] = system
            };
        }

        private static IEnumerable<Document> EnumerateDocuments(UIApplication app)
        {
            foreach (Document document in app.Application.Documents)
            {
                yield return document;
            }
        }

        private static Document ResolveDocument(UIApplication app, BridgeRequestEnvelope request)
        {
            string requestedFingerprint = request.DocumentFingerprint ?? GetString(request.Payload, "documentFingerprint");
            Document activeDocument = app.ActiveUIDocument?.Document;

            if (string.IsNullOrWhiteSpace(requestedFingerprint))
            {
                return activeDocument ?? EnumerateDocuments(app).FirstOrDefault();
            }

            return EnumerateDocuments(app)
                .FirstOrDefault(document => string.Equals(ComputeDocumentFingerprint(document), requestedFingerprint, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetDocumentId(Document document)
        {
            return string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title + ":" + document.GetHashCode().ToString(CultureInfo.InvariantCulture)
                : document.PathName;
        }

        private static string ComputeDocumentFingerprint(Document document)
        {
            string raw = document.Title + "|" + document.PathName + "|" + document.GetHashCode().ToString(CultureInfo.InvariantCulture);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            }
        }

        private static View SafeActiveView(Document document)
        {
            try
            {
                return document.ActiveView;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeElementName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch
            {
                return string.Empty;
            }
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

        private static bool TryParseBuiltInCategory(string value, out BuiltInCategory category)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (Enum.TryParse(trimmed, true, out category)) return true;
            return Enum.TryParse("OST_" + trimmed.Replace(" ", string.Empty), true, out category);
        }

        private static Type ResolveElementType(string className)
        {
            string normalized = (className ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            Type type = typeof(Element).Assembly.GetType("Autodesk.Revit.DB." + normalized, false, true);
            return type != null && typeof(Element).IsAssignableFrom(type) ? type : null;
        }

        private static ElementId CreateElementId(string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long idValue))
            {
                throw new ArgumentException("Element id must be numeric: " + value);
            }

            try
            {
                return (ElementId)Activator.CreateInstance(typeof(ElementId), idValue);
            }
            catch
            {
                return (ElementId)Activator.CreateInstance(typeof(ElementId), Convert.ToInt32(idValue));
            }
        }

        private static bool IsValidElementId(ElementId id)
        {
            return id != null && GetElementIdValue(id) >= 0;
        }

        private static string ToElementIdString(ElementId id)
        {
            return id == null ? string.Empty : GetElementIdValue(id).ToString(CultureInfo.InvariantCulture);
        }

        private static string ToWorksetIdString(WorksetId id)
        {
            if (id == null) return string.Empty;

            object value = typeof(WorksetId).GetProperty("IntegerValue")?.GetValue(id, null);
            return value == null ? id.ToString() : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static long GetElementIdValue(ElementId id)
        {
            object value = typeof(ElementId).GetProperty("Value")?.GetValue(id, null);
            if (value != null) return Convert.ToInt64(value, CultureInfo.InvariantCulture);

#pragma warning disable CS0618
            return id.IntegerValue;
#pragma warning restore CS0618
        }

        private static int ParseCursor(string cursor, List<BridgeWarning> warnings)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            if (int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset) && offset >= 0) return offset;

            warnings.Add(new BridgeWarning
            {
                Code = "INVALID_CURSOR",
                Message = "Cursor '" + cursor + "' is invalid; returning the first page."
            });
            return 0;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? value as Dictionary<string, object> : null;
        }

        private static IReadOnlyList<string> GetStringList(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return Array.Empty<string>();
            if (value is string single) return new[] { single };
            if (value is object[] array) return array.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            if (value is ArrayList list) return list.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return Array.Empty<string>();
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static int? GetInt(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> root, string key, bool defaultValue)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return defaultValue;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static BridgeResponseEnvelope Success(
            BridgeRequestEnvelope request,
            object data,
            Stopwatch sw,
            List<BridgeWarning> warnings = null,
            BridgeMetrics metrics = null)
        {
            sw.Stop();
            BridgeMetrics actualMetrics = metrics ?? new BridgeMetrics();
            actualMetrics.ElapsedMs = sw.ElapsedMilliseconds;
            return new BridgeResponseEnvelope
            {
                Ok = true,
                RequestId = request.RequestId,
                Data = data,
                Warnings = warnings ?? new List<BridgeWarning>(),
                Metrics = actualMetrics,
                Generation = 0
            };
        }

        private static BridgeResponseEnvelope Failure(BridgeRequestEnvelope request, string code, string message, Stopwatch sw = null)
        {
            sw?.Stop();
            return new BridgeResponseEnvelope
            {
                Ok = false,
                RequestId = request.RequestId,
                Error = new BridgeError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true
                },
                Metrics = new BridgeMetrics { ElapsedMs = sw?.ElapsedMilliseconds ?? 0 }
            };
        }
    }
}
