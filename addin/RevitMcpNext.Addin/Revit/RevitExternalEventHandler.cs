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
    internal sealed class RevitExternalEventHandler : IExternalEventHandler
    {
        private const string AddinVersion = "0.1.0";
        private const int MaxItemsPerExternalEvent = 16;
        private const int MaxQueryLimit = 500;
        private const int MaxViewLimit = 500;
        private const int MaxSheetLimit = 500;
        private const int MaxParameterElementLimit = 100;
        private const int MaxParameterLimit = 200;
        private const int MaxCatalogLimit = 200;
        private const int MaxStatisticsBucketLimit = 200;
        private const int MaxStatisticsScanLimit = 100000;
        private const int MaxModelContextLimit = 200;
        private const int MaxMaterialLimit = 200;
        private const int MaxMaterialScanLimit = 100000;
        private const int MaxWarningLimit = 200;
        private const int MaxWarningElementIds = 128;
        private const int MaxRoomLimit = 500;
        private const int MaxChangeSetOperations = 50;
        private const long MaxFamilyLoadBytes = 100L * 1024L * 1024L;
        private const int DefaultDeleteDependentLimit = 25;
        private const int MaxDeleteDependentLimit = 256;
        private static readonly IReadOnlyDictionary<string, string> ExpectedOperationKinds =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "read",
                ["list_documents"] = "read",
                ["create_project_from_template"] = "write",
                ["get_levels"] = "read",
                ["get_views"] = "read",
                ["get_sheets"] = "read",
                ["get_current_view"] = "read",
                ["get_current_view_elements"] = "read",
                ["get_selection"] = "read",
                ["analyze_model"] = "read",
                ["get_model_readiness"] = "read",
                ["get_model_context"] = "read",
                ["get_material_quantities"] = "read",
                ["get_warnings"] = "read",
                ["get_rooms"] = "read",
                ["catalog"] = "read",
                ["query"] = "read",
                ["describe_parameters"] = "read",
                ["preview_change_set"] = "preview",
                ["apply_change_set"] = "write",
                ["cancel_request"] = "debug"
            };
        private readonly RevitRequestQueue _queue;
        private readonly TransactionService _transactions;
        private readonly DocumentGenerationTracker _generations;
        private readonly PreviewTokenStore _previewTokens;

        public RevitExternalEventHandler(
            RevitRequestQueue queue,
            TransactionService transactions,
            DocumentGenerationTracker generations = null,
            PreviewTokenStore previewTokens = null)
        {
            _queue = queue;
            _transactions = transactions;
            _generations = generations ?? new DocumentGenerationTracker();
            _previewTokens = previewTokens ?? new PreviewTokenStore();
        }

        public void Execute(UIApplication app)
        {
            int processed = 0;
            while (processed < MaxItemsPerExternalEvent && _queue.TryDequeue(out RevitRequestQueue.WorkItem item))
            {
                processed++;
                item.TrySetResult(Handle(app, item.Envelope));
            }

            if (_queue.HasPending)
            {
                _queue.Raise();
            }
        }

        public string GetName()
        {
            return "Revit MCP Next External Event Handler";
        }

        internal BridgeResponseEnvelope HandleDirect(UIApplication app, BridgeRequestEnvelope request)
        {
            return Handle(app, request);
        }

        private long GetActiveDocumentGeneration(UIApplication app)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            return activeDocument == null ? 0 : _generations.GetGeneration(activeDocument);
        }

        private BridgeResponseEnvelope ValidateExpectedGeneration(
            BridgeRequestEnvelope request,
            Document document,
            Stopwatch sw,
            out long generation)
        {
            generation = _generations.GetGeneration(document);
            long? expectedGeneration =
                request.ExpectedGeneration ??
                GetLong(request.Payload, "expectedGeneration") ??
                GetLong(request.Payload, "baseGeneration");

            if (expectedGeneration.HasValue && expectedGeneration.Value != generation)
            {
                return Failure(
                    request,
                    "GENERATION_MISMATCH",
                    "The document generation is " + generation.ToString(CultureInfo.InvariantCulture) +
                    " but the request expected " + expectedGeneration.Value.ToString(CultureInfo.InvariantCulture) + ". Refresh the document state before retrying.",
                    sw);
            }

            return null;
        }

        private BridgeResponseEnvelope Handle(UIApplication app, BridgeRequestEnvelope request)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                BridgeResponseEnvelope operationKindFailure = ValidateOperationKind(request, sw);
                if (operationKindFailure != null) return operationKindFailure;

                return _transactions.Read(() =>
                {
                    switch (request.Operation)
                    {
                        case "status":
                            return Success(request, BuildStatus(app), sw, generation: GetActiveDocumentGeneration(app));
                        case "list_documents":
                            return Success(request, BuildDocumentList(app), sw, generation: GetActiveDocumentGeneration(app));
                        case "create_project_from_template":
                            return HandleCreateProjectFromTemplate(app, request, sw);
                        case "get_levels":
                            return HandleGetLevels(app, request, sw);
                        case "get_views":
                            return HandleGetViews(app, request, sw);
                        case "get_sheets":
                            return HandleGetSheets(app, request, sw);
                        case "get_current_view":
                            return HandleGetCurrentView(app, request, sw);
                        case "get_current_view_elements":
                            return HandleGetCurrentViewElements(app, request, sw);
                        case "get_selection":
                            return HandleGetSelection(app, request, sw);
                        case "analyze_model":
                            return HandleAnalyzeModel(app, request, sw);
                        case "get_model_readiness":
                            return HandleGetModelReadiness(app, request, sw);
                        case "get_model_context":
                            return HandleGetModelContext(app, request, sw);
                        case "get_material_quantities":
                            return HandleGetMaterialQuantities(app, request, sw);
                        case "get_warnings":
                            return HandleGetWarnings(app, request, sw);
                        case "get_rooms":
                            return HandleGetRooms(app, request, sw);
                        case "catalog":
                            return HandleCatalog(app, request, sw);
                        case "query":
                            return HandleQuery(app, request, sw);
                        case "describe_parameters":
                            return HandleDescribeParameters(app, request, sw);
                        case "preview_change_set":
                            return HandlePreviewChange(app, request, sw);
                        case "apply_change_set":
                            return HandleApplyChange(app, request, sw);
                        case "cancel_request":
                            return HandleCancel(request, sw);
                        default:
                            return Failure(request, "UNSUPPORTED_OPERATION", "Unsupported Revit MCP operation: " + request.Operation, sw);
                    }
                });
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("Revit command failed. requestId=" + request.RequestId + " operation=" + request.Operation, ex);
                return Failure(request, "REVIT_COMMAND_FAILED", ex.Message, sw);
            }
        }

        private static BridgeResponseEnvelope ValidateOperationKind(BridgeRequestEnvelope request, Stopwatch sw)
        {
            if (request == null) return null;
            if (!ExpectedOperationKinds.TryGetValue(request.Operation ?? string.Empty, out string expectedKind))
            {
                return null;
            }

            string actualKind = string.IsNullOrWhiteSpace(request.OperationKind) ? "read" : request.OperationKind.Trim();
            if (string.Equals(actualKind, expectedKind, StringComparison.Ordinal))
            {
                return null;
            }

            return Failure(
                request,
                "OPERATION_KIND_MISMATCH",
                "Bridge request operation '" + request.Operation + "' must use operationKind '" + expectedKind + "' but received '" + actualKind + "'.",
                sw);
        }

        private BridgeResponseEnvelope HandleGetLevels(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_levels.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

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
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetViews(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_views.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            int limit = Math.Min(MaxViewLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeCropBox = GetBool(payload, "includeCropBox", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeViewFields(GetStringList(payload, "fields"), preset, includeCropBox, warnings);

            List<View> materialized = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !(view is ViewSheet))
                .Where(view => MatchesViewFilter(view, filter))
                .OrderBy(view => view.ViewType.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(SafeElementName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(view => GetElementIdValue(view.Id))
                .ToList();

            int totalCount = materialized.Count;
            List<View> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(view => BuildViewItem(document, view, fields, includeCropBox)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["scope"] = "views",
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
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetSheets(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_sheets.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            int limit = Math.Min(MaxSheetLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includePlacedViews = GetBool(payload, "includePlacedViews", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeSheetFields(GetStringList(payload, "fields"), preset, includePlacedViews, warnings);

            List<ViewSheet> materialized = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(sheet => MatchesSheetFilter(document, sheet, filter))
                .OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(SafeElementName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(sheet => GetElementIdValue(sheet.Id))
                .ToList();

            int totalCount = materialized.Count;
            List<ViewSheet> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(sheet => BuildSheetItem(document, sheet, fields, includePlacedViews)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["scope"] = "sheets",
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
                },
                generation: generation);
        }

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

        private BridgeResponseEnvelope HandleDescribeParameters(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.describe_parameters.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            string preset = NormalizeParameterDescribePreset(GetString(payload, "preset"));
            int limit = Math.Min(MaxParameterElementLimit, Math.Max(1, GetInt(payload, "limit") ?? DefaultParameterElementLimit(preset)));
            int parameterLimit = Math.Min(MaxParameterLimit, Math.Max(1, GetInt(payload, "parameterLimit") ?? DefaultParameterLimit(preset)));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeTypeParameters = GetBool(payload, "includeTypeParameters", DefaultIncludeTypeParameters(preset));
            bool includeReadOnly = GetBool(payload, "includeReadOnly", DefaultIncludeReadOnlyParameters(preset));
            bool includeValues = GetBool(payload, "includeValues", DefaultIncludeParameterValues(preset));
            string nameContains = GetString(payload, "nameContains");

            string scope;
            IEnumerable<Element> elements = CreateFilteredElements(app, document, filter, warnings, out scope);
            PageResult<Element> pageResult = PageItems(elements, offset, limit, includeTotalCount);
            List<Element> page = pageResult.Items;
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(element => BuildParameterTarget(
                    document,
                    element,
                    includeTypeParameters,
                    includeReadOnly,
                    includeValues,
                    nameContains,
                    parameterLimit)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = pageResult.Truncated,
                ["parameterLimit"] = parameterLimit,
                ["preset"] = preset,
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

        private BridgeResponseEnvelope HandleGetCurrentView(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_current_view.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            View view = SafeActiveView(document);
            if (view == null)
            {
                return Failure(request, "NO_ACTIVE_VIEW", "The active Revit document does not expose an active view.", sw);
            }

            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            bool includeCropBox = GetBool(payload, "includeCropBox", false);
            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["view"] = BuildViewInfo(document, view, includeCropBox),
                ["source"] = "revit-addin"
            };

            return Success(
                request,
                data,
                sw,
                metrics: new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    ReturnedCount = 1,
                    TotalCount = 1
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetCurrentViewElements(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_current_view_elements.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            View view = SafeActiveView(document);
            if (view == null)
            {
                return Failure(request, "NO_ACTIVE_VIEW", "The active Revit document does not expose an active view.", sw);
            }

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            filter["viewId"] = ToElementIdString(view.Id);

            if (GetBool(payload, "includeHidden", false))
            {
                warnings.Add(new BridgeWarning
                {
                    Code = "INCLUDE_HIDDEN_LIMITED",
                    Message = "Revit view collectors only return elements visible to the collector; hidden element expansion is not available in this release."
                });
            }

            return HandleScopedElementList(
                app,
                request,
                sw,
                document,
                generation,
                payload,
                filter,
                warnings,
                "activeView",
                view,
                includeSelection: false);
        }

        private BridgeResponseEnvelope HandleGetSelection(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_selection.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            filter["selectionOnly"] = true;

            return HandleScopedElementList(
                app,
                request,
                sw,
                document,
                generation,
                payload,
                filter,
                warnings,
                "selection",
                null,
                includeSelection: true);
        }

        private BridgeResponseEnvelope HandleAnalyzeModel(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.analyze_model.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            int bucketLimit = Math.Min(MaxStatisticsBucketLimit, Math.Max(1, GetInt(payload, "bucketLimit") ?? 50));
            int maxElementsScanned = Math.Min(MaxStatisticsScanLimit, Math.Max(100, GetInt(payload, "maxElementsScanned") ?? 50000));
            bool includeCategoryBreakdown = GetBool(payload, "includeCategoryBreakdown", true);
            bool includeClassBreakdown = GetBool(payload, "includeClassBreakdown", true);
            bool includeLevelBreakdown = GetBool(payload, "includeLevelBreakdown", true);

            var collectorSw = Stopwatch.StartNew();
            bool truncated = false;
            var elements = new List<Element>();
            foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
            {
                if (elements.Count >= maxElementsScanned)
                {
                    truncated = true;
                    break;
                }

                elements.Add(element);
            }

            if (truncated)
            {
                warnings.Add(new BridgeWarning
                {
                    Code = "MODEL_STATISTICS_TRUNCATED",
                    Message = "Model statistics were computed from the first " + elements.Count.ToString(CultureInfo.InvariantCulture) + " non-type elements. Increase maxElementsScanned for a deeper scan."
                });
            }

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["totals"] = new Dictionary<string, object>
                {
                    ["elements"] = elements.Count,
                    ["modelElements"] = elements.Count(IsModelElement),
                    ["elementTypes"] = CountCollectorElements(new FilteredElementCollector(document).WhereElementIsElementType()),
                    ["families"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(Family))),
                    ["views"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(View))),
                    ["sheets"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(ViewSheet))),
                    ["levels"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(Level))),
                    ["materials"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(Material)))
                },
                ["scannedElements"] = elements.Count,
                ["bucketLimit"] = bucketLimit,
                ["truncated"] = truncated,
                ["source"] = "revit-addin"
            };

            if (includeCategoryBreakdown) data["byCategory"] = BuildCategoryBuckets(elements, bucketLimit);
            if (includeClassBreakdown) data["byClass"] = BuildClassBuckets(elements, bucketLimit);
            if (includeLevelBreakdown) data["byLevel"] = BuildLevelBuckets(document, elements, bucketLimit);
            collectorSw.Stop();

            return Success(
                request,
                data,
                sw,
                warnings,
                new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = elements.Count,
                    TotalCount = truncated ? (int?)null : elements.Count
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetModelReadiness(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_model_readiness.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> data = BuildModelReadiness(app, document, generation, request.Payload);
            collectorSw.Stop();

            return Success(
                request,
                data,
                sw,
                metrics: new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = 1,
                    TotalCount = 1
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetModelContext(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_model_context.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            bool includeProjectInfo = GetBool(payload, "includeProjectInfo", true);
            bool includePhases = GetBool(payload, "includePhases", true);
            bool includeWorksets = GetBool(payload, "includeWorksets", true);
            bool includeDesignOptions = GetBool(payload, "includeDesignOptions", true);
            bool includeRevitLinks = GetBool(payload, "includeRevitLinks", true);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            int phaseLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "phaseLimit") ?? 50));
            int worksetLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "worksetLimit") ?? 50));
            int designOptionLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "designOptionLimit") ?? 50));
            int revitLinkLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "revitLinkLimit") ?? 50));

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["source"] = "revit-addin"
            };

            if (includeProjectInfo) data["projectInfo"] = BuildProjectInfoSummary(document.ProjectInformation);
            if (includePhases) data["phases"] = BuildContextSection(BuildPhaseSummaries(document), phaseLimit, includeTotalCount);
            if (includeWorksets) data["worksets"] = BuildWorksetSection(document, worksetLimit, includeTotalCount);
            if (includeDesignOptions) data["designOptions"] = BuildContextSection(BuildDesignOptionSummaries(document), designOptionLimit, includeTotalCount);
            if (includeRevitLinks) data["revitLinks"] = BuildContextSection(BuildRevitLinkSummaries(document), revitLinkLimit, includeTotalCount);
            collectorSw.Stop();

            return Success(
                request,
                data,
                sw,
                new List<BridgeWarning>(),
                new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetMaterialQuantities(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_material_quantities.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
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
                warnings.Add(new BridgeWarning
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
                new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = includeTotalCount ? materialized.Count : (int?)null
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetRooms(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_rooms.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            int limit = Math.Min(MaxRoomLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeUnplaced = GetBool(payload, "includeUnplaced", false);
            string[] fields = NormalizeRoomFields(GetStringList(payload, "fields"), GetString(payload, "preset"), warnings);

            List<Room> materialized = CreateRoomElements(document, filter, warnings)
                .Where(room => includeUnplaced || IsRoomPlaced(room))
                .Where(room => MatchesRoomFilter(document, room, filter))
                .OrderBy(room => GetRoomLevelName(document, room), StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetRoomNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(SafeElementName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(room => GetElementIdValue(room.Id))
                .ToList();
            int totalCount = materialized.Count;
            List<Room> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(room => BuildRoomItem(document, room, fields)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["units"] = new Dictionary<string, object>
                {
                    ["area"] = "m2",
                    ["volume"] = "m3",
                    ["location"] = "mm"
                },
                ["scope"] = "rooms",
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
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetWarnings(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_warnings.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            int limit = Math.Min(MaxWarningLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            string[] fields = NormalizeWarningFields(GetStringList(payload, "fields"), GetString(payload, "preset"), warnings);

            List<FailureMessage> materialized = document.GetWarnings()
                .Where(failure => MatchesWarningFilter(failure, filter))
                .OrderBy(failure => WarningSeverity(failure), StringComparer.OrdinalIgnoreCase)
                .ThenBy(failure => WarningDescription(failure), StringComparer.OrdinalIgnoreCase)
                .ThenBy(failure => WarningDefinitionId(failure), StringComparer.OrdinalIgnoreCase)
                .ThenBy(failure => FirstWarningElementId(failure), StringComparer.OrdinalIgnoreCase)
                .ToList();

            PageResult<FailureMessage> pageResult = PageItems(materialized, offset, limit, includeTotalCount);
            List<FailureMessage> page = pageResult.Items;
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select((failure, index) => BuildWarningItem(failure, fields, offset + index)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = pageResult.Truncated,
                ["fields"] = fields,
                ["scope"] = "warnings",
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

        private BridgeResponseEnvelope HandleScopedElementList(
            UIApplication app,
            BridgeRequestEnvelope request,
            Stopwatch sw,
            Document document,
            long generation,
            Dictionary<string, object> payload,
            Dictionary<string, object> filter,
            List<BridgeWarning> warnings,
            string scopeOverride,
            View view,
            bool includeSelection)
        {
            var collectorSw = Stopwatch.StartNew();
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
                ["document"] = BuildDocumentReference(document, generation),
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
                ["scope"] = string.IsNullOrWhiteSpace(scopeOverride) ? scope : scopeOverride,
                ["source"] = "revit-addin"
            };

            if (view != null) data["view"] = BuildViewSummary(view);
            if (includeSelection)
            {
                UIDocument uidocument = app.ActiveUIDocument;
                bool available = uidocument != null && ReferenceEquals(uidocument.Document, document);
                data["selection"] = new Dictionary<string, object>
                {
                    ["count"] = available ? uidocument.Selection.GetElementIds().Count : 0,
                    ["available"] = available
                };
            }

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

        private BridgeResponseEnvelope HandleCatalog(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.catalog.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
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
                new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandlePreviewChange(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.preview_change_set.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> operations = GetOperations(payload);
            if (operations.Count == 0)
            {
                return Failure(request, "EMPTY_CHANGE_SET", "A change set must contain at least one operation.", sw);
            }
            if (operations.Count > MaxChangeSetOperations)
            {
                return Failure(request, "CHANGE_SET_TOO_LARGE", "A change set can contain at most 50 operations.", sw);
            }

            string transactionName = GetTransactionName(payload);
            string documentFingerprint = ComputeDocumentFingerprint(document);
            string previewId = ComputePreviewId(document, transactionName, operations);
            var changes = new List<Dictionary<string, object>>();
            var validationContext = new PreviewValidationContext();
            bool ready = true;
            string riskLevel = "low";

            for (int index = 0; index < operations.Count; index++)
            {
                Dictionary<string, object> change = PreviewOperation(document, operations[index], index, validationContext);
                changes.Add(change);
                string status = GetString(change, "status");
                if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase)) ready = false;
                string operationType = GetString(operations[index], "type");
                if (string.Equals(operationType, "create_level", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_wall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_grid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_floor", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_room", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "place_family_instance", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_sheet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "place_view_on_sheet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_text_note", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "load_family", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "tag_room", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "tag_element", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "copy_element", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "change_element_type", StringComparison.OrdinalIgnoreCase))
                {
                    riskLevel = "medium";
                }
                if (string.Equals(operationType, "delete_element", StringComparison.OrdinalIgnoreCase))
                {
                    riskLevel = "high";
                }
            }

            string operationsHash = ComputeChangeSetHash(operations);
            string changesHash = ComputeChangeSetHash(changes);
            string changeSetHash = ComputePreviewChangeSetHash(documentFingerprint, generation, transactionName, operationsHash, changesHash);
            PreviewToken token = _previewTokens.Issue(
                previewId,
                documentFingerprint,
                generation,
                transactionName,
                operationsHash,
                changesHash,
                changeSetHash,
                ready,
                operations.Count);

            var data = new Dictionary<string, object>
            {
                ["previewId"] = previewId,
                ["documentFingerprint"] = documentFingerprint,
                ["changeSetHash"] = changeSetHash,
                ["baseGeneration"] = generation,
                ["generation"] = generation,
                ["transactionName"] = transactionName,
                ["operationCount"] = operations.Count,
                ["ready"] = ready,
                ["requiresConfirmation"] = true,
                ["expiresAt"] = FormatUtc(token.ExpiresAtUtc),
                ["previewExpiresAtUtc"] = FormatUtc(token.ExpiresAtUtc),
                ["riskLevel"] = riskLevel,
                ["changes"] = changes.ToArray()
            };

            return Success(request, data, sw, warnings, new BridgeMetrics
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                ReturnedCount = changes.Count,
                TotalCount = changes.Count
            }, generation: generation);
        }

        private BridgeResponseEnvelope HandleCreateProjectFromTemplate(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            if (!GetBool(payload, "confirm", false))
            {
                return Failure(request, "CONFIRMATION_REQUIRED", "revit.create_project_from_template requires confirm=true because it creates or overwrites a local RVT file.", sw);
            }

            string templatePathRaw = GetString(payload, "templatePath");
            string outputPathRaw = GetString(payload, "outputPath");
            if (string.IsNullOrWhiteSpace(templatePathRaw))
            {
                return Failure(request, "TEMPLATE_PATH_REQUIRED", "templatePath is required and must point to a local .rte file.", sw);
            }
            if (string.IsNullOrWhiteSpace(outputPathRaw))
            {
                return Failure(request, "OUTPUT_PATH_REQUIRED", "outputPath is required and must point to a disposable .rvt file.", sw);
            }

            string templatePath;
            string outputPath;
            try
            {
                templatePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(templatePathRaw.Trim()));
                outputPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(outputPathRaw.Trim()));
            }
            catch (Exception ex)
            {
                return Failure(request, "INVALID_MODEL_PATH", "templatePath and outputPath must be valid local paths. " + ex.Message, sw);
            }

            if (!string.Equals(Path.GetExtension(templatePath), ".rte", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(request, "TEMPLATE_EXTENSION_REQUIRED", "templatePath must point to a Revit template file (.rte): " + templatePath, sw);
            }
            if (!string.Equals(Path.GetExtension(outputPath), ".rvt", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(request, "OUTPUT_EXTENSION_REQUIRED", "outputPath must point to a Revit project file (.rvt): " + outputPath, sw);
            }
            if (!File.Exists(templatePath))
            {
                return Failure(request, "TEMPLATE_NOT_FOUND", "Revit template file was not found: " + templatePath, sw);
            }

            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Failure(request, "OUTPUT_DIRECTORY_REQUIRED", "outputPath must include a parent directory: " + outputPath, sw);
            }

            bool overwrite = GetBool(payload, "overwrite", false);
            bool outputExists = File.Exists(outputPath);
            if (outputExists && !overwrite)
            {
                return Failure(request, "OUTPUT_ALREADY_EXISTS", "Output RVT already exists. Pass overwrite=true only for a known disposable fixture: " + outputPath, sw);
            }

            Document createdDocument = null;
            try
            {
                Directory.CreateDirectory(outputDirectory);
                createdDocument = app.Application.NewProjectDocument(templatePath);
                if (createdDocument == null)
                {
                    return Failure(request, "PROJECT_CREATE_FAILED", "Revit did not create a project document from template: " + templatePath, sw);
                }

                var saveAsOptions = new SaveAsOptions
                {
                    OverwriteExistingFile = overwrite
                };
                createdDocument.SaveAs(outputPath, saveAsOptions);

                Document resultDocument = ActivateCreatedProject(app, createdDocument, outputPath);
                createdDocument = resultDocument;
                Document activeDocument = app.ActiveUIDocument?.Document;
                bool activated = ReferenceEquals(resultDocument, activeDocument);
                long generation = _generations.GetGeneration(resultDocument);
                var data = new Dictionary<string, object>
                {
                    ["templatePath"] = templatePath,
                    ["outputPath"] = outputPath,
                    ["overwritten"] = outputExists,
                    ["activated"] = activated,
                    ["document"] = BuildDocumentSummary(resultDocument, activeDocument),
                    ["source"] = "revit-api"
                };

                return Success(request, data, sw, generation: generation);
            }
            catch
            {
                if (createdDocument != null && string.IsNullOrWhiteSpace(createdDocument.PathName))
                {
                    try
                    {
                        createdDocument.Close(false);
                    }
                    catch
                    {
                        // Preserve the original Revit API failure.
                    }
                }

                throw;
            }
        }

        private static Document ActivateCreatedProject(UIApplication app, Document createdDocument, string outputPath)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            if (ReferenceEquals(createdDocument, activeDocument))
            {
                return createdDocument;
            }

            if (createdDocument != null)
            {
                createdDocument.Close(false);
            }

            UIDocument activatedDocument = app.OpenAndActivateDocument(outputPath);
            if (activatedDocument?.Document == null)
            {
                throw new InvalidOperationException("Revit created the project but did not activate it in the UI: " + outputPath);
            }

            return activatedDocument.Document;
        }

        private BridgeResponseEnvelope HandleApplyChange(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.apply_change_set.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            if (!GetBool(payload, "confirm", false))
            {
                return Failure(request, "CONFIRMATION_REQUIRED", "revit.apply_change_set requires confirm=true.", sw);
            }

            string providedPreviewId = GetString(payload, "previewId");
            string providedChangeSetHash = GetString(payload, "changeSetHash");
            string providedExpiresAt = GetString(payload, "expiresAt");
            long? providedBaseGeneration = GetLong(payload, "baseGeneration");
            if (string.IsNullOrWhiteSpace(providedPreviewId) ||
                string.IsNullOrWhiteSpace(providedChangeSetHash) ||
                string.IsNullOrWhiteSpace(providedExpiresAt) ||
                !providedBaseGeneration.HasValue)
            {
                return Failure(request, "PREVIEW_METADATA_REQUIRED", "revit.apply_change_set requires previewId, baseGeneration, changeSetHash, and expiresAt from revit.preview_change_set.", sw);
            }

            if (!DateTimeOffset.TryParse(providedExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedExpiresAt))
            {
                return Failure(request, "PREVIEW_EXPIRES_AT_INVALID", "revit.apply_change_set expiresAt must be the ISO 8601 timestamp returned by revit.preview_change_set.", sw);
            }

            if (parsedExpiresAt <= DateTimeOffset.UtcNow)
            {
                return Failure(request, "PREVIEW_EXPIRED", "The preview has expired. Run revit.preview_change_set again before applying.", sw);
            }

            List<Dictionary<string, object>> operations = GetOperations(payload);
            if (operations.Count == 0)
            {
                return Failure(request, "EMPTY_CHANGE_SET", "A change set must contain at least one operation.", sw);
            }
            if (operations.Count > MaxChangeSetOperations)
            {
                return Failure(request, "CHANGE_SET_TOO_LARGE", "A change set can contain at most 50 operations.", sw);
            }

            string transactionName = GetTransactionName(payload);
            string documentFingerprint = ComputeDocumentFingerprint(document);
            string expectedPreviewId = ComputePreviewId(document, transactionName, operations);
            if (!string.Equals(providedPreviewId, expectedPreviewId, StringComparison.Ordinal))
            {
                return Failure(request, "PREVIEW_ID_MISMATCH", "The supplied previewId does not match the current change set and document.", sw);
            }

            PreviewTokenValidation metadataValidation = _previewTokens.ValidateMetadata(
                providedPreviewId,
                documentFingerprint,
                generation,
                providedChangeSetHash);
            if (!metadataValidation.Ok)
            {
                return Failure(request, metadataValidation.Code, metadataValidation.Message, sw);
            }

            var previewChanges = new List<Dictionary<string, object>>();
            var validationContext = new PreviewValidationContext();
            for (int index = 0; index < operations.Count; index++)
            {
                Dictionary<string, object> change = PreviewOperation(document, operations[index], index, validationContext);
                previewChanges.Add(change);
            }

            PreviewTokenValidation tokenValidation = _previewTokens.Validate(
                providedPreviewId,
                documentFingerprint,
                generation,
                transactionName,
                ComputeChangeSetHash(operations),
                ComputeChangeSetHash(previewChanges),
                providedChangeSetHash);
            if (!tokenValidation.Ok)
            {
                return Failure(request, tokenValidation.Code, tokenValidation.Message, sw);
            }

            List<Dictionary<string, object>> appliedChanges = _transactions.Write(document, transactionName, () =>
            {
                var results = new List<Dictionary<string, object>>();
                for (int index = 0; index < operations.Count; index++)
                {
                    results.Add(ApplyOperation(document, operations[index], index));
                }
                return results;
            });
            _previewTokens.Consume(providedPreviewId);
            long appliedGeneration = _generations.GetGeneration(document);

            var data = new Dictionary<string, object>
            {
                ["previewId"] = expectedPreviewId,
                ["documentFingerprint"] = documentFingerprint,
                ["changeSetHash"] = tokenValidation.Token.ChangeSetHash,
                ["baseGeneration"] = tokenValidation.Token.Generation,
                ["generation"] = appliedGeneration,
                ["transactionName"] = transactionName,
                ["applied"] = true,
                ["changedCount"] = appliedChanges.Count,
                ["changes"] = appliedChanges.ToArray()
            };

            return Success(request, data, sw, metrics: new BridgeMetrics
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                ReturnedCount = appliedChanges.Count,
                TotalCount = appliedChanges.Count
            }, generation: appliedGeneration);
        }

        private static BridgeResponseEnvelope HandleCancel(BridgeRequestEnvelope request, Stopwatch sw)
        {
            var data = new Dictionary<string, object>
            {
                ["cancelled"] = false,
                ["message"] = "No queued cancellable request matched. In-flight Revit API work cannot be interrupted safely."
            };

            string requestId = GetString(request.Payload, "requestId");
            if (!string.IsNullOrWhiteSpace(requestId)) data["requestId"] = requestId;

            return Success(request, data, sw);
        }

        private static Dictionary<string, object> PreviewOperation(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string type = GetString(operation, "type");
            switch (type)
            {
                case "set_parameter":
                    return PreviewSetParameter(document, operation, index);
                case "create_level":
                    return PreviewCreateLevel(document, operation, index, validationContext);
                case "create_wall":
                    return PreviewCreateWall(document, operation, index);
                case "move_element":
                    return PreviewMoveElement(document, operation, index);
                case "rotate_element":
                    return PreviewRotateElement(document, operation, index);
                case "copy_element":
                    return PreviewCopyElement(document, operation, index);
                case "change_element_type":
                    return PreviewChangeElementType(document, operation, index);
                case "set_element_pinned":
                    return PreviewSetElementPinned(document, operation, index);
                case "create_grid":
                    return PreviewCreateGrid(document, operation, index, validationContext);
                case "create_floor":
                    return PreviewCreateFloor(document, operation, index);
                case "create_room":
                    return PreviewCreateRoom(document, operation, index, validationContext);
                case "place_family_instance":
                    return PreviewPlaceFamilyInstance(document, operation, index);
                case "create_sheet":
                    return PreviewCreateSheet(document, operation, index, validationContext);
                case "place_view_on_sheet":
                    return PreviewPlaceViewOnSheet(document, operation, index);
                case "create_text_note":
                    return PreviewCreateTextNote(document, operation, index);
                case "load_family":
                    return PreviewLoadFamily(document, operation, index);
                case "tag_room":
                    return PreviewTagRoom(document, operation, index);
                case "tag_element":
                    return PreviewTagElement(document, operation, index);
                case "delete_element":
                    return PreviewDeleteElement(document, operation, index);
                default:
                    return BlockedChange(operation, index, "Unsupported change operation type: " + (type ?? "(missing)"));
            }
        }

        private static Dictionary<string, object> ApplyOperation(Document document, Dictionary<string, object> operation, int index)
        {
            string type = GetString(operation, "type");
            switch (type)
            {
                case "set_parameter":
                    return ApplySetParameter(document, operation, index);
                case "create_level":
                    return ApplyCreateLevel(document, operation, index);
                case "create_wall":
                    return ApplyCreateWall(document, operation, index);
                case "move_element":
                    return ApplyMoveElement(document, operation, index);
                case "rotate_element":
                    return ApplyRotateElement(document, operation, index);
                case "copy_element":
                    return ApplyCopyElement(document, operation, index);
                case "change_element_type":
                    return ApplyChangeElementType(document, operation, index);
                case "set_element_pinned":
                    return ApplySetElementPinned(document, operation, index);
                case "create_grid":
                    return ApplyCreateGrid(document, operation, index);
                case "create_floor":
                    return ApplyCreateFloor(document, operation, index);
                case "create_room":
                    return ApplyCreateRoom(document, operation, index);
                case "place_family_instance":
                    return ApplyPlaceFamilyInstance(document, operation, index);
                case "create_sheet":
                    return ApplyCreateSheet(document, operation, index);
                case "place_view_on_sheet":
                    return ApplyPlaceViewOnSheet(document, operation, index);
                case "create_text_note":
                    return ApplyCreateTextNote(document, operation, index);
                case "load_family":
                    return ApplyLoadFamily(document, operation, index);
                case "tag_room":
                    return ApplyTagRoom(document, operation, index);
                case "tag_element":
                    return ApplyTagElement(document, operation, index);
                case "delete_element":
                    return ApplyDeleteElement(document, operation, index);
                default:
                    throw new InvalidOperationException("Unsupported change operation type: " + (type ?? "(missing)"));
            }
        }

        private static Dictionary<string, object> PreviewSetParameter(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string parameterName = GetString(operation, "parameterName");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "set_parameter requires elementId.");
            if (string.IsNullOrWhiteSpace(parameterName)) return BlockedChange(operation, index, "set_parameter requires parameterName.");
            if (!operation.TryGetValue("value", out object value)) return BlockedChange(operation, index, "set_parameter requires value.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);

            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null) return BlockedChange(operation, index, "Parameter '" + parameterName + "' was not found on element " + elementId + ".");
            if (parameter.IsReadOnly) return BlockedChange(operation, index, "Parameter '" + parameterName + "' is read-only.");

            return Change(operation, index, "ready", ElementTarget(element, parameterName),
                before: ParameterSnapshot(parameter),
                after: new Dictionary<string, object>
                {
                    ["value"] = value,
                    ["storageType"] = parameter.StorageType.ToString()
                });
        }

        private static Dictionary<string, object> ApplySetParameter(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewSetParameter(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "set_parameter preview failed.");
            }

            string elementId = GetString(operation, "elementId");
            string parameterName = GetString(operation, "parameterName");
            Element element = ResolveElement(document, elementId);
            Parameter parameter = element.LookupParameter(parameterName);
            object before = ParameterValue(parameter);
            SetParameterValue(parameter, operation["value"]);
            object after = ParameterValue(parameter);

            return Change(operation, index, "applied", ElementTarget(element, parameterName),
                before: new Dictionary<string, object>
                {
                    ["value"] = before,
                    ["storageType"] = parameter.StorageType.ToString()
                },
                after: new Dictionary<string, object>
                {
                    ["value"] = after,
                    ["storageType"] = parameter.StorageType.ToString()
                });
        }

        private static Dictionary<string, object> PreviewCreateLevel(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string name = GetString(operation, "name");
            Dictionary<string, object> elevation = GetDictionary(operation, "elevation");
            if (string.IsNullOrWhiteSpace(name)) return BlockedChange(operation, index, "create_level requires name.");
            if (elevation == null) return BlockedChange(operation, index, "create_level requires elevation.");
            if (LevelNameExists(document, name)) return BlockedChange(operation, index, "A level named '" + name + "' already exists.");
            if (validationContext != null && !validationContext.TryAddLevelName(name))
            {
                return BlockedChange(operation, index, "The change set creates duplicate level name '" + name + "'.");
            }

            double internalElevation;
            try
            {
                internalElevation = ToInternalElevation(elevation);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            return Change(operation, index, "ready",
                target: new Dictionary<string, object> { ["document"] = document.Title },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(internalElevation, UnitTypeId.Millimeters), "mm", "metric")
                });
        }

        private static Dictionary<string, object> ApplyCreateLevel(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateLevel(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_level preview failed.");
            }

            string name = GetString(operation, "name");
            double internalElevation = ToInternalElevation(GetDictionary(operation, "elevation"));
            Level level = Level.Create(document, internalElevation);
            level.Name = name;

            return Change(operation, index, "applied",
                target: ElementTarget(level, null),
                before: null,
                after: new Dictionary<string, object>
                {
                    ["id"] = ToElementIdString(level.Id),
                    ["uniqueId"] = level.UniqueId,
                    ["name"] = level.Name,
                    ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters), "mm", "metric")
                });
        }

        private static Dictionary<string, object> PreviewCreateWall(Document document, Dictionary<string, object> operation, int index)
        {
            string levelId = GetString(operation, "levelId");
            Dictionary<string, object> startValue = GetDictionary(operation, "start");
            Dictionary<string, object> endValue = GetDictionary(operation, "end");
            if (string.IsNullOrWhiteSpace(levelId)) return BlockedChange(operation, index, "create_wall requires levelId.");
            if (startValue == null) return BlockedChange(operation, index, "create_wall requires start.");
            if (endValue == null) return BlockedChange(operation, index, "create_wall requires end.");

            Level level = ResolveElement(document, levelId) as Level;
            if (level == null) return BlockedChange(operation, index, "Level " + levelId + " was not found.");

            string wallTypeId = GetString(operation, "wallTypeId");
            WallType wallType = null;
            if (!string.IsNullOrWhiteSpace(wallTypeId))
            {
                wallType = ResolveElement(document, wallTypeId) as WallType;
                if (wallType == null) return BlockedChange(operation, index, "Wall type " + wallTypeId + " was not found.");
            }

            XYZ start;
            XYZ end;
            double? height = null;
            try
            {
                start = ToInternalPoint(startValue, "start");
                end = ToInternalPoint(endValue, "end");
                Dictionary<string, object> heightValue = GetDictionary(operation, "height");
                if (heightValue != null)
                {
                    height = ToInternalLength(heightValue, "height");
                    if (height.Value <= 0) return BlockedChange(operation, index, "create_wall height must be greater than zero.");
                }
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string geometryError = ValidateWallBaseline(document, start, end);
            if (!string.IsNullOrWhiteSpace(geometryError)) return BlockedChange(operation, index, geometryError);

            bool structural = GetBool(operation, "structural", false);
            bool flip = GetBool(operation, "flip", false);
            double baseOffset = start.Z - level.Elevation;
            var target = new Dictionary<string, object>
            {
                ["document"] = document.Title,
                ["levelId"] = ToElementIdString(level.Id),
                ["levelName"] = level.Name
            };
            if (wallType != null)
            {
                target["wallTypeId"] = ToElementIdString(wallType.Id);
                target["wallTypeName"] = SafeElementName(wallType);
            }

            var after = new Dictionary<string, object>
            {
                ["levelId"] = ToElementIdString(level.Id),
                ["start"] = PointValue(start),
                ["end"] = PointValue(end),
                ["length"] = LengthValue(start.DistanceTo(end)),
                ["baseOffset"] = LengthValue(baseOffset),
                ["structural"] = structural,
                ["flip"] = flip
            };
            if (wallType != null) after["wallTypeId"] = ToElementIdString(wallType.Id);
            if (height.HasValue) after["height"] = LengthValue(height.Value);

            return Change(operation, index, "ready", target, before: null, after: after);
        }

        private static Dictionary<string, object> ApplyCreateWall(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateWall(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_wall preview failed.");
            }

            Level level = ResolveElement(document, GetString(operation, "levelId")) as Level;
            XYZ start = ToInternalPoint(GetDictionary(operation, "start"), "start");
            XYZ end = ToInternalPoint(GetDictionary(operation, "end"), "end");
            bool structural = GetBool(operation, "structural", false);
            bool flip = GetBool(operation, "flip", false);
            Wall wall = Wall.Create(document, Line.CreateBound(start, end), level.Id, structural);

            string wallTypeId = GetString(operation, "wallTypeId");
            if (!string.IsNullOrWhiteSpace(wallTypeId))
            {
                WallType wallType = ResolveElement(document, wallTypeId) as WallType;
                ElementId changedId = wall.ChangeTypeId(wallType.Id);
                if (IsValidElementId(changedId) &&
                    !string.Equals(ToElementIdString(changedId), ToElementIdString(wall.Id), StringComparison.Ordinal))
                {
                    Wall changedWall = document.GetElement(changedId) as Wall;
                    if (changedWall != null) wall = changedWall;
                }
            }

            SetWallDoubleParameter(wall, BuiltInParameter.WALL_BASE_OFFSET, start.Z - level.Elevation, "base offset");

            Dictionary<string, object> heightValue = GetDictionary(operation, "height");
            if (heightValue != null)
            {
                SetWallDoubleParameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM, ToInternalLength(heightValue, "height"), "height");
            }

            if (flip)
            {
                wall.Flip();
            }

            return Change(operation, index, "applied",
                target: ElementTarget(wall, null),
                before: null,
                after: WallSnapshot(wall));
        }

        private static Dictionary<string, object> PreviewCreateGrid(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            Dictionary<string, object> startValue = GetDictionary(operation, "start");
            Dictionary<string, object> endValue = GetDictionary(operation, "end");
            if (startValue == null) return BlockedChange(operation, index, "create_grid requires start.");
            if (endValue == null) return BlockedChange(operation, index, "create_grid requires end.");

            string name = GetString(operation, "name");
            if (!string.IsNullOrWhiteSpace(name) && GridNameExists(document, name))
            {
                return BlockedChange(operation, index, "A grid named '" + name + "' already exists.");
            }
            if (!string.IsNullOrWhiteSpace(name) && validationContext != null && !validationContext.TryAddGridName(name))
            {
                return BlockedChange(operation, index, "The change set creates duplicate grid name '" + name + "'.");
            }

            XYZ start;
            XYZ end;
            try
            {
                start = ToInternalPoint(startValue, "start");
                end = ToInternalPoint(endValue, "end");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string geometryError = ValidateLinearDatum(document, start, end, "create_grid");
            if (!string.IsNullOrWhiteSpace(geometryError)) return BlockedChange(operation, index, geometryError);

            var after = new Dictionary<string, object>
            {
                ["start"] = PointValue(start),
                ["end"] = PointValue(end),
                ["length"] = LengthValue(start.DistanceTo(end))
            };
            if (!string.IsNullOrWhiteSpace(name)) after["name"] = name.Trim();

            return Change(operation, index, "ready",
                target: new Dictionary<string, object> { ["document"] = document.Title },
                before: null,
                after: after);
        }

        private static Dictionary<string, object> ApplyCreateGrid(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateGrid(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_grid preview failed.");
            }

            XYZ start = ToInternalPoint(GetDictionary(operation, "start"), "start");
            XYZ end = ToInternalPoint(GetDictionary(operation, "end"), "end");
            Grid grid = Grid.Create(document, Line.CreateBound(start, end));
            string name = GetString(operation, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                grid.Name = name.Trim();
            }

            return Change(operation, index, "applied",
                target: ElementTarget(grid, null),
                before: null,
                after: GridSnapshot(grid));
        }

        private static Dictionary<string, object> PreviewCreateFloor(Document document, Dictionary<string, object> operation, int index)
        {
            string levelId = GetString(operation, "levelId");
            if (string.IsNullOrWhiteSpace(levelId)) return BlockedChange(operation, index, "create_floor requires levelId.");

            Level level = ResolveElement(document, levelId) as Level;
            if (level == null) return BlockedChange(operation, index, "Level " + levelId + " was not found.");

            FloorType floorType = ResolveFloorType(document, GetString(operation, "floorTypeId"));
            if (floorType == null) return BlockedChange(operation, index, "A usable floor type was not found.");

            List<Dictionary<string, object>> outline = GetPointList(operation, "outline");
            if (outline.Count < 3) return BlockedChange(operation, index, "create_floor requires at least three outline points.");

            List<XYZ> points;
            try
            {
                points = ToInternalPointList(outline, "outline");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string outlineError = ValidateFloorOutline(document, level, points);
            if (!string.IsNullOrWhiteSpace(outlineError)) return BlockedChange(operation, index, outlineError);

            bool structural = GetBool(operation, "structural", false);
            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["document"] = document.Title,
                    ["levelId"] = ToElementIdString(level.Id),
                    ["levelName"] = level.Name,
                    ["floorTypeId"] = ToElementIdString(floorType.Id),
                    ["floorTypeName"] = SafeElementName(floorType)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["levelId"] = ToElementIdString(level.Id),
                    ["floorTypeId"] = ToElementIdString(floorType.Id),
                    ["outline"] = PointArrayValue(NormalizeClosedLoop(points)),
                    ["area"] = AreaValue(PolygonAreaInternal(NormalizeClosedLoop(points))),
                    ["structural"] = structural
                });
        }

        private static Dictionary<string, object> ApplyCreateFloor(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateFloor(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_floor preview failed.");
            }

            Level level = ResolveElement(document, GetString(operation, "levelId")) as Level;
            FloorType floorType = ResolveFloorType(document, GetString(operation, "floorTypeId"));
            List<XYZ> points = NormalizeClosedLoop(ToInternalPointList(GetPointList(operation, "outline"), "outline"));
            CurveLoop loop = BuildCurveLoop(points);
            bool structural = GetBool(operation, "structural", false);
            Floor floor = Floor.Create(document, new List<CurveLoop> { loop }, floorType.Id, level.Id, structural, null, 0.0);

            return Change(operation, index, "applied",
                target: ElementTarget(floor, null),
                before: null,
                after: FloorSnapshot(floor, points));
        }

        private static Dictionary<string, object> PreviewCreateRoom(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string levelId = GetString(operation, "levelId");
            Dictionary<string, object> locationValue = GetDictionary(operation, "location");
            if (string.IsNullOrWhiteSpace(levelId)) return BlockedChange(operation, index, "create_room requires levelId.");
            if (locationValue == null) return BlockedChange(operation, index, "create_room requires location.");

            Level level = ResolveElement(document, levelId) as Level;
            if (level == null) return BlockedChange(operation, index, "Level " + levelId + " was not found.");

            UV location;
            try
            {
                location = ToInternalUv(locationValue, "location");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string number = NormalizeOptionalText(GetString(operation, "number"));
            bool allowDuplicateNumber = GetBool(operation, "allowDuplicateNumber", false);
            if (!string.IsNullOrWhiteSpace(number) && !allowDuplicateNumber)
            {
                if (RoomNumberExists(document, number))
                {
                    return BlockedChange(operation, index, "A room numbered '" + number + "' already exists.");
                }
                if (validationContext != null && !validationContext.TryAddRoomNumber(number))
                {
                    return BlockedChange(operation, index, "The change set creates duplicate room number '" + number + "'.");
                }
            }

            Room existingRoom = document.GetRoomAtPoint(new XYZ(location.U, location.V, level.Elevation));
            if (existingRoom != null)
            {
                return BlockedChange(operation, index, "Room " + GetRoomNumber(existingRoom) + " already contains the requested location.");
            }

            var after = new Dictionary<string, object>
            {
                ["levelId"] = ToElementIdString(level.Id),
                ["levelName"] = level.Name,
                ["location"] = Point2Value(location),
                ["allowDuplicateNumber"] = allowDuplicateNumber
            };

            string name = NormalizeOptionalText(GetString(operation, "name"));
            string department = NormalizeOptionalText(GetString(operation, "department"));
            if (!string.IsNullOrWhiteSpace(name)) after["name"] = name;
            if (!string.IsNullOrWhiteSpace(number)) after["number"] = number;
            if (!string.IsNullOrWhiteSpace(department)) after["department"] = department;

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["document"] = document.Title,
                    ["levelId"] = ToElementIdString(level.Id),
                    ["levelName"] = level.Name
                },
                before: null,
                after: after);
        }

        private static Dictionary<string, object> ApplyCreateRoom(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateRoom(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_room preview failed.");
            }

            Level level = ResolveElement(document, GetString(operation, "levelId")) as Level;
            UV location = ToInternalUv(GetDictionary(operation, "location"), "location");
            Room room = document.Create.NewRoom(level, location);
            if (room == null)
            {
                throw new InvalidOperationException("Revit did not create a room at the requested location.");
            }

            SetRoomStringParameter(room, BuiltInParameter.ROOM_NAME, NormalizeOptionalText(GetString(operation, "name")));
            SetRoomStringParameter(room, BuiltInParameter.ROOM_NUMBER, NormalizeOptionalText(GetString(operation, "number")));
            SetRoomStringParameter(room, BuiltInParameter.ROOM_DEPARTMENT, NormalizeOptionalText(GetString(operation, "department")));

            return Change(operation, index, "applied",
                target: ElementTarget(room, null),
                before: null,
                after: RoomSnapshot(room));
        }

        private static Dictionary<string, object> PreviewPlaceFamilyInstance(Document document, Dictionary<string, object> operation, int index)
        {
            FamilyPlacementRequest placement;
            string error;
            if (!TryBuildFamilyPlacementRequest(document, operation, out placement, out error))
            {
                return BlockedChange(operation, index, error);
            }

            return Change(operation, index, "ready",
                target: FamilyPlacementTarget(document, placement),
                before: null,
                after: FamilyPlacementPreviewSnapshot(placement));
        }

        private static Dictionary<string, object> ApplyPlaceFamilyInstance(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewPlaceFamilyInstance(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "place_family_instance preview failed.");
            }

            FamilyPlacementRequest placement;
            string error;
            if (!TryBuildFamilyPlacementRequest(document, operation, out placement, out error))
            {
                throw new InvalidOperationException(error ?? "place_family_instance validation failed.");
            }

            bool activatedSymbol = false;
            if (!placement.Symbol.IsActive)
            {
                placement.Symbol.Activate();
                document.Regenerate();
                activatedSymbol = true;
            }

            FamilyInstance instance;
            if (string.Equals(placement.PlacementMode, "wallHosted", StringComparison.OrdinalIgnoreCase))
            {
                instance = placement.Level == null
                    ? document.Create.NewFamilyInstance(placement.Location, placement.Symbol, placement.Host, StructuralType.NonStructural)
                    : document.Create.NewFamilyInstance(placement.Location, placement.Symbol, placement.Host, placement.Level, StructuralType.NonStructural);
            }
            else
            {
                instance = document.Create.NewFamilyInstance(placement.Location, placement.Symbol, placement.Level, StructuralType.NonStructural);
            }

            if (instance == null)
            {
                throw new InvalidOperationException("Revit did not create a family instance for place_family_instance.");
            }

            ApplyFamilyInstancePostPlacementTransforms(document, instance, placement);
            document.Regenerate();
            FamilyInstance created = document.GetElement(instance.Id) as FamilyInstance ?? instance;
            Dictionary<string, object> snapshot = FamilyInstanceSnapshot(created);
            snapshot["activatedSymbol"] = activatedSymbol;
            snapshot["placementMode"] = placement.PlacementMode;

            return Change(operation, index, "applied",
                target: ElementTarget(created, null),
                before: null,
                after: snapshot);
        }

        private static Dictionary<string, object> PreviewCreateSheet(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string sheetNumber = NormalizeOptionalText(GetString(operation, "sheetNumber"));
            if (string.IsNullOrWhiteSpace(sheetNumber)) return BlockedChange(operation, index, "create_sheet requires sheetNumber.");
            if (SheetNumberExists(document, sheetNumber)) return BlockedChange(operation, index, "A sheet numbered '" + sheetNumber + "' already exists.");
            if (validationContext != null && !validationContext.TryAddSheetNumber(sheetNumber))
            {
                return BlockedChange(operation, index, "The change set creates duplicate sheet number '" + sheetNumber + "'.");
            }

            FamilySymbol titleBlockType = null;
            string titleBlockTypeId = GetString(operation, "titleBlockTypeId");
            if (!string.IsNullOrWhiteSpace(titleBlockTypeId))
            {
                titleBlockType = ResolveTitleBlockType(document, titleBlockTypeId);
                if (titleBlockType == null) return BlockedChange(operation, index, "Title block type " + titleBlockTypeId + " was not found.");
            }

            var target = new Dictionary<string, object>
            {
                ["document"] = document.Title,
                ["sheetNumber"] = sheetNumber
            };
            if (titleBlockType != null) target["titleBlockType"] = ElementSummary(document, titleBlockType);

            var after = new Dictionary<string, object>
            {
                ["sheetNumber"] = sheetNumber,
                ["name"] = NormalizeOptionalText(GetString(operation, "name"))
            };
            if (titleBlockType != null)
            {
                after["titleBlockTypeId"] = ToElementIdString(titleBlockType.Id);
                after["titleBlockTypeName"] = SafeElementName(titleBlockType);
            }

            return Change(operation, index, "ready", target, before: null, after: after);
        }

        private static Dictionary<string, object> ApplyCreateSheet(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateSheet(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_sheet preview failed.");
            }

            FamilySymbol titleBlockType = ResolveTitleBlockType(document, GetString(operation, "titleBlockTypeId"));
            ElementId titleBlockTypeId = titleBlockType == null ? ElementId.InvalidElementId : titleBlockType.Id;
            ViewSheet sheet = ViewSheet.Create(document, titleBlockTypeId);
            if (sheet == null)
            {
                throw new InvalidOperationException("Revit did not create a sheet.");
            }

            sheet.SheetNumber = NormalizeOptionalText(GetString(operation, "sheetNumber"));
            string name = NormalizeOptionalText(GetString(operation, "name"));
            if (!string.IsNullOrWhiteSpace(name)) sheet.Name = name;

            return Change(operation, index, "applied",
                target: ElementTarget(sheet, null),
                before: null,
                after: SheetSnapshot(document, sheet));
        }

        private static Dictionary<string, object> PreviewPlaceViewOnSheet(Document document, Dictionary<string, object> operation, int index)
        {
            string sheetId = GetString(operation, "sheetId");
            string viewId = GetString(operation, "viewId");
            Dictionary<string, object> centerValue = GetDictionary(operation, "center");
            if (string.IsNullOrWhiteSpace(sheetId)) return BlockedChange(operation, index, "place_view_on_sheet requires sheetId.");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "place_view_on_sheet requires viewId.");
            if (centerValue == null) return BlockedChange(operation, index, "place_view_on_sheet requires center.");

            ViewSheet sheet = ResolveElement(document, sheetId) as ViewSheet;
            if (sheet == null) return BlockedChange(operation, index, "Sheet " + sheetId + " was not found.");

            View view = ResolveElement(document, viewId) as View;
            if (view == null) return BlockedChange(operation, index, "View " + viewId + " was not found.");
            if (view is ViewSheet) return BlockedChange(operation, index, "place_view_on_sheet cannot place a sheet on a sheet.");
            if (view.IsTemplate) return BlockedChange(operation, index, "View " + viewId + " is a template and cannot be placed on a sheet.");

            XYZ center;
            try
            {
                center = ToInternalSheetPoint(centerValue, "center");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            bool canAdd;
            try
            {
                canAdd = Viewport.CanAddViewToSheet(document, sheet.Id, view.Id);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, "Revit could not validate sheet placement: " + ex.Message);
            }

            if (!canAdd)
            {
                return BlockedChange(operation, index, "View " + viewId + " cannot be placed on sheet " + sheetId + ". It may already be placed, be unsupported, or be incompatible with viewport placement.");
            }

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["sheet"] = SheetSnapshot(document, sheet),
                    ["view"] = BuildViewSummary(view)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["sheetId"] = ToElementIdString(sheet.Id),
                    ["viewId"] = ToElementIdString(view.Id),
                    ["center"] = PointValue(center)
                });
        }

        private static Dictionary<string, object> ApplyPlaceViewOnSheet(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewPlaceViewOnSheet(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "place_view_on_sheet preview failed.");
            }

            ViewSheet sheet = ResolveElement(document, GetString(operation, "sheetId")) as ViewSheet;
            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            XYZ center = ToInternalSheetPoint(GetDictionary(operation, "center"), "center");
            Viewport viewport = Viewport.Create(document, sheet.Id, view.Id, center);
            if (viewport == null)
            {
                throw new InvalidOperationException("Revit did not create a viewport for place_view_on_sheet.");
            }

            return Change(operation, index, "applied",
                target: ElementTarget(viewport, null),
                before: null,
                after: ViewportSnapshot(document, viewport));
        }

        private static Dictionary<string, object> PreviewCreateTextNote(Document document, Dictionary<string, object> operation, int index)
        {
            string viewId = GetString(operation, "viewId");
            string text = GetString(operation, "text");
            Dictionary<string, object> positionValue = GetDictionary(operation, "position");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "create_text_note requires viewId.");
            if (string.IsNullOrWhiteSpace(text)) return BlockedChange(operation, index, "create_text_note requires text.");
            if (text.Length > 2048) return BlockedChange(operation, index, "create_text_note text can contain at most 2048 characters.");
            if (positionValue == null) return BlockedChange(operation, index, "create_text_note requires position.");

            View view = ResolveElement(document, viewId) as View;
            string viewError = ValidateTextNoteView(view);
            if (!string.IsNullOrWhiteSpace(viewError)) return BlockedChange(operation, index, viewError);

            TextNoteType textNoteType = ResolveTextNoteType(document, GetString(operation, "textNoteTypeId"));
            if (textNoteType == null) return BlockedChange(operation, index, "A usable TextNoteType was not found.");

            XYZ position;
            double? width = null;
            double rotationRadians = 0;
            bool hasRotation = false;
            try
            {
                position = ToInternalPoint(positionValue, "position");
                Dictionary<string, object> widthValue = GetDictionary(operation, "width");
                if (widthValue != null)
                {
                    width = ToInternalLength(widthValue, "width");
                    if (width.Value <= 0) return BlockedChange(operation, index, "create_text_note width must be greater than zero.");
                }

                Dictionary<string, object> rotationValue = GetDictionary(operation, "rotation");
                if (rotationValue != null)
                {
                    rotationRadians = ToInternalAngle(rotationValue);
                    if (!IsFinite(rotationRadians)) return BlockedChange(operation, index, "create_text_note rotation must be a finite angle.");
                    hasRotation = true;
                }
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            var after = new Dictionary<string, object>
            {
                ["viewId"] = ToElementIdString(view.Id),
                ["viewName"] = SafeElementName(view),
                ["textNoteTypeId"] = ToElementIdString(textNoteType.Id),
                ["textNoteTypeName"] = SafeElementName(textNoteType),
                ["textLength"] = text.Length,
                ["position"] = PointValue(position)
            };
            if (width.HasValue) after["width"] = LengthValue(width.Value);
            if (hasRotation) after["rotation"] = AngleValue(rotationRadians);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["view"] = BuildViewSummary(view),
                    ["textNoteType"] = ElementSummary(document, textNoteType)
                },
                before: null,
                after: after);
        }

        private static Dictionary<string, object> ApplyCreateTextNote(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateTextNote(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_text_note preview failed.");
            }

            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            TextNoteType textNoteType = ResolveTextNoteType(document, GetString(operation, "textNoteTypeId"));
            XYZ position = ToInternalPoint(GetDictionary(operation, "position"), "position");
            TextNoteOptions options = new TextNoteOptions(textNoteType.Id);
            Dictionary<string, object> rotationValue = GetDictionary(operation, "rotation");
            if (rotationValue != null)
            {
                options.Rotation = ToInternalAngle(rotationValue);
            }

            TextNote textNote;
            Dictionary<string, object> widthValue = GetDictionary(operation, "width");
            if (widthValue != null)
            {
                textNote = TextNote.Create(document, view.Id, position, ToInternalLength(widthValue, "width"), GetString(operation, "text"), options);
            }
            else
            {
                textNote = TextNote.Create(document, view.Id, position, GetString(operation, "text"), options);
            }

            if (textNote == null)
            {
                throw new InvalidOperationException("Revit did not create a text note.");
            }

            return Change(operation, index, "applied",
                target: ElementTarget(textNote, null),
                before: null,
                after: TextNoteSnapshot(document, textNote));
        }

        private static Dictionary<string, object> PreviewLoadFamily(Document document, Dictionary<string, object> operation, int index)
        {
            if (!TryBuildFamilyLoadRequest(operation, out FamilyLoadRequest request, out string error))
            {
                return BlockedChange(operation, index, error);
            }

            Family existingFamily = FindFamilyByName(document, request.FamilyName);
            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["document"] = document.Title,
                    ["familyPath"] = request.FullPath,
                    ["fileName"] = request.FileName,
                    ["familyName"] = request.FamilyName,
                    ["expectedSha256"] = request.ExpectedSha256,
                    ["existingFamilyId"] = existingFamily == null ? null : ToElementIdString(existingFamily.Id)
                },
                before: existingFamily == null ? null : FamilyLoadSnapshot(document, existingFamily, existing: true),
                after: new Dictionary<string, object>
                {
                    ["familyPath"] = request.FullPath,
                    ["fileName"] = request.FileName,
                    ["fileSizeBytes"] = request.FileSizeBytes,
                    ["fileSha256"] = request.FileSha256,
                    ["familyName"] = request.FamilyName,
                    ["allowedCategories"] = request.AllowedCategories.ToArray(),
                    ["overwriteParameterValues"] = request.OverwriteParameterValues,
                    ["allowNetworkPath"] = request.AllowNetworkPath
                });
        }

        private static Dictionary<string, object> ApplyLoadFamily(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewLoadFamily(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "load_family preview failed.");
            }

            if (!TryBuildFamilyLoadRequest(operation, out FamilyLoadRequest request, out string error))
            {
                throw new InvalidOperationException(error);
            }

            Family before = FindFamilyByName(document, request.FamilyName);
            Family loadedFamily = null;
            bool loadReturned = document.LoadFamily(
                request.FullPath,
                new ControlledFamilyLoadOptions(request.OverwriteParameterValues),
                out loadedFamily);

            Family family = loadedFamily ?? FindFamilyByName(document, request.FamilyName) ?? before;
            if (family == null)
            {
                throw new InvalidOperationException("Revit did not report a loaded family for " + request.FileName + ".");
            }

            FamilySymbol[] symbols = GetFamilySymbols(document, family).ToArray();
            if (request.AllowedCategories.Count > 0 && !MatchesAllowedFamilyCategories(family, symbols, request.AllowedCategories))
            {
                throw new InvalidOperationException(
                    "Loaded family '" + SafeElementName(family) + "' did not match allowedCategories: " +
                    string.Join(", ", request.AllowedCategories));
            }

            Dictionary<string, object> after = FamilyLoadSnapshot(document, family, existing: false);
            after["familyPath"] = request.FullPath;
            after["fileName"] = request.FileName;
            after["fileSizeBytes"] = request.FileSizeBytes;
            after["fileSha256"] = request.FileSha256;
            after["loadReturned"] = loadReturned;
            after["allowedCategories"] = request.AllowedCategories.ToArray();
            after["overwriteParameterValues"] = request.OverwriteParameterValues;

            return Change(operation, index, "applied",
                target: ElementTarget(family, null),
                before: before == null ? null : FamilyLoadSnapshot(document, before, existing: true),
                after: after);
        }

        private static bool TryBuildFamilyLoadRequest(Dictionary<string, object> operation, out FamilyLoadRequest request, out string error)
        {
            request = null;
            error = null;

            string familyPath = GetString(operation, "familyPath");
            if (string.IsNullOrWhiteSpace(familyPath))
            {
                error = "load_family requires familyPath.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(familyPath.Trim()));
            }
            catch (Exception ex)
            {
                error = "familyPath is not a valid local path: " + ex.Message;
                return false;
            }

            if (!Path.IsPathRooted(fullPath))
            {
                error = "load_family requires an absolute familyPath.";
                return false;
            }

            bool allowNetworkPath = GetBool(operation, "allowNetworkPath", false);
            if (!allowNetworkPath && IsNetworkPath(fullPath))
            {
                error = "load_family does not allow UNC/network paths unless allowNetworkPath is true.";
                return false;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".rfa", StringComparison.OrdinalIgnoreCase))
            {
                error = "load_family only accepts Revit .rfa family files.";
                return false;
            }

            FileInfo file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                error = "Family file was not found: " + fullPath;
                return false;
            }

            if (file.Length <= 0)
            {
                error = "Family file is empty: " + fullPath;
                return false;
            }

            if (file.Length > MaxFamilyLoadBytes)
            {
                error = "Family file exceeds the 100 MB load_family limit: " + fullPath;
                return false;
            }

            string fileSha256;
            try
            {
                fileSha256 = ComputeFileSha256(file.FullName);
            }
            catch (Exception ex)
            {
                error = "Could not hash family file: " + ex.Message;
                return false;
            }

            string expectedSha256;
            try
            {
                expectedSha256 = NormalizeSha256(GetString(operation, "expectedSha256"));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !string.Equals(expectedSha256, fileSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "Family file SHA-256 did not match expectedSha256.";
                return false;
            }

            List<string> allowedCategories = GetStringList(operation, "allowedCategories")
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();

            request = new FamilyLoadRequest(
                file.FullName,
                file.Name,
                Path.GetFileNameWithoutExtension(file.Name),
                file.Length,
                "sha256:" + fileSha256,
                string.IsNullOrWhiteSpace(expectedSha256) ? null : "sha256:" + expectedSha256,
                allowedCategories,
                GetBool(operation, "overwriteParameterValues", false),
                allowNetworkPath);
            return true;
        }

        private static bool IsNetworkPath(string fullPath)
        {
            return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                   fullPath.StartsWith("//", StringComparison.Ordinal);
        }

        private static string NormalizeSha256(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("sha256:".Length);
            }

            if (string.IsNullOrWhiteSpace(normalized)) return null;
            normalized = normalized.ToLowerInvariant();
            if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            {
                throw new ArgumentException("expectedSha256 must be a 64-character hex SHA-256 digest.");
            }

            return normalized;
        }

        private static string ComputeFileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static Family FindFamilyByName(Document document, string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return null;
            return new FilteredElementCollector(document)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(family => string.Equals(SafeElementName(family), familyName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<FamilySymbol> GetFamilySymbols(Document document, Family family)
        {
            if (family == null) return Enumerable.Empty<FamilySymbol>();
            return family.GetFamilySymbolIds()
                .Select(id => document.GetElement(id) as FamilySymbol)
                .Where(symbol => symbol != null);
        }

        private static Dictionary<string, object> FamilyLoadSnapshot(Document document, Family family, bool existing)
        {
            FamilySymbol[] symbols = GetFamilySymbols(document, family).ToArray();
            var snapshot = new Dictionary<string, object>
            {
                ["familyId"] = ToElementIdString(family.Id),
                ["familyName"] = SafeElementName(family),
                ["existing"] = existing,
                ["symbolCount"] = symbols.Length,
                ["symbols"] = symbols
                    .Take(50)
                    .Select(symbol => FamilySymbolLoadSnapshot(symbol))
                    .ToArray()
            };

            if (family.Category != null) snapshot["category"] = family.Category.Name;
            if (family.FamilyCategory != null) snapshot["familyCategory"] = family.FamilyCategory.Name;
            return snapshot;
        }

        private static Dictionary<string, object> FamilySymbolLoadSnapshot(FamilySymbol symbol)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(symbol.Id),
                ["uniqueId"] = symbol.UniqueId,
                ["class"] = symbol.GetType().Name,
                ["name"] = SafeElementName(symbol),
                ["familyName"] = GetFamilyName(symbol),
                ["isActive"] = symbol.IsActive
            };

            string builtInCategory = GetBuiltInCategoryName(symbol);
            if (!string.IsNullOrWhiteSpace(builtInCategory)) snapshot["builtInCategory"] = builtInCategory;
            if (symbol.Category != null) snapshot["category"] = symbol.Category.Name;
            string placementType = GetPlacementType(symbol);
            if (!string.IsNullOrWhiteSpace(placementType)) snapshot["placementType"] = placementType;
            return snapshot;
        }

        private static bool MatchesAllowedFamilyCategories(Family family, IEnumerable<FamilySymbol> symbols, IReadOnlyList<string> allowedCategories)
        {
            if (allowedCategories == null || allowedCategories.Count == 0) return true;
            HashSet<string> allowed = new HashSet<string>(allowedCategories, StringComparer.OrdinalIgnoreCase);
            foreach (string token in FamilyCategoryTokens(family, symbols))
            {
                if (allowed.Contains(token)) return true;
            }

            return false;
        }

        private static IEnumerable<string> FamilyCategoryTokens(Family family, IEnumerable<FamilySymbol> symbols)
        {
            if (family?.Category != null) yield return family.Category.Name;
            if (family?.FamilyCategory != null)
            {
                yield return family.FamilyCategory.Name;
                string builtInFamilyCategory = string.Empty;
                try
                {
                    builtInFamilyCategory = ((BuiltInCategory)GetElementIdValue(family.FamilyCategory.Id)).ToString();
                }
                catch
                {
                    // Some custom family categories cannot be mapped to BuiltInCategory.
                }
                if (!string.IsNullOrWhiteSpace(builtInFamilyCategory)) yield return builtInFamilyCategory;
            }

            foreach (FamilySymbol symbol in symbols ?? Enumerable.Empty<FamilySymbol>())
            {
                string builtInCategory = GetBuiltInCategoryName(symbol);
                if (!string.IsNullOrWhiteSpace(builtInCategory)) yield return builtInCategory;
                if (symbol.Category != null) yield return symbol.Category.Name;
            }
        }

        private static Dictionary<string, object> PreviewTagRoom(Document document, Dictionary<string, object> operation, int index)
        {
            string roomId = GetString(operation, "roomId");
            string viewId = GetString(operation, "viewId");
            Dictionary<string, object> locationValue = GetDictionary(operation, "location");
            if (string.IsNullOrWhiteSpace(roomId)) return BlockedChange(operation, index, "tag_room requires roomId.");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "tag_room requires viewId.");
            if (locationValue == null) return BlockedChange(operation, index, "tag_room requires location.");

            Room room = ResolveElement(document, roomId) as Room;
            if (room == null) return BlockedChange(operation, index, "Room " + roomId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, room, roomId, "expectedUniqueId", "Room");
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (!IsRoomPlaced(room)) return BlockedChange(operation, index, "Room " + roomId + " is unplaced and cannot be tagged.");

            View view = ResolveElement(document, viewId) as View;
            string viewError = ValidateRoomTagView(view);
            if (!string.IsNullOrWhiteSpace(viewError)) return BlockedChange(operation, index, viewError);

            Element tagType = ResolveRoomTagTypeElement(document, GetString(operation, "tagTypeId"));
            if (tagType == null) return BlockedChange(operation, index, "A usable room tag type was not found.");
            if (HasRoomTagInView(document, room, view))
            {
                return BlockedChange(operation, index, "Room " + roomId + " already has a room tag in view " + viewId + ".");
            }

            UV location;
            SpatialElementTagOrientation orientation;
            try
            {
                location = ToInternalUv(locationValue, "location");
                orientation = ParseRoomTagOrientation(GetString(operation, "orientation"));
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            bool hasLeader = GetBool(operation, "hasLeader", false);
            string probeError = ProbeRoomTagCreation(document, room, view, tagType, location, hasLeader, orientation);
            if (!string.IsNullOrWhiteSpace(probeError)) return BlockedChange(operation, index, probeError);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["room"] = RoomSnapshot(room),
                    ["view"] = BuildViewSummary(view),
                    ["tagType"] = ElementSummary(document, tagType)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["roomId"] = ToElementIdString(room.Id),
                    ["viewId"] = ToElementIdString(view.Id),
                    ["tagTypeId"] = ToElementIdString(tagType.Id),
                    ["tagTypeName"] = SafeElementName(tagType),
                    ["location"] = Point2Value(location),
                    ["hasLeader"] = hasLeader,
                    ["orientation"] = orientation.ToString()
                });
        }

        private static Dictionary<string, object> ApplyTagRoom(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewTagRoom(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "tag_room preview failed.");
            }

            Room room = ResolveElement(document, GetString(operation, "roomId")) as Room;
            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            Element tagType = ResolveRoomTagTypeElement(document, GetString(operation, "tagTypeId"));
            UV location = ToInternalUv(GetDictionary(operation, "location"), "location");

            RoomTag tag = document.Create.NewRoomTag(new LinkElementId(room.Id), location, view.Id);
            if (tag == null)
            {
                throw new InvalidOperationException("Revit did not create a room tag.");
            }

            if (tagType != null)
            {
                tag.ChangeTypeId(tagType.Id);
            }
            tag.HasLeader = GetBool(operation, "hasLeader", false);
            tag.TagOrientation = ParseRoomTagOrientation(GetString(operation, "orientation"));
            document.Regenerate();

            return Change(operation, index, "applied",
                target: ElementTarget(tag, null),
                before: null,
                after: RoomTagSnapshot(document, tag));
        }

        private static Dictionary<string, object> PreviewTagElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string viewId = GetString(operation, "viewId");
            string tagTypeId = GetString(operation, "tagTypeId");
            Dictionary<string, object> positionValue = GetDictionary(operation, "position");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "tag_element requires elementId.");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "tag_element requires viewId.");
            if (string.IsNullOrWhiteSpace(tagTypeId)) return BlockedChange(operation, index, "tag_element requires tagTypeId.");
            if (positionValue == null) return BlockedChange(operation, index, "tag_element requires position.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            string targetError = ValidateIndependentTagTarget(element);
            if (!string.IsNullOrWhiteSpace(targetError)) return BlockedChange(operation, index, targetError);

            View view = ResolveElement(document, viewId) as View;
            string viewError = ValidateIndependentTagView(view);
            if (!string.IsNullOrWhiteSpace(viewError)) return BlockedChange(operation, index, viewError);
            if (!IsElementVisibleInView(document, element, view))
            {
                return BlockedChange(operation, index, "Element " + elementId + " is not visible in view " + viewId + ".");
            }

            FamilySymbol tagType = ResolveIndependentTagType(document, tagTypeId);
            if (tagType == null) return BlockedChange(operation, index, "tagTypeId must reference a non-material tag FamilySymbol.");
            if (HasIndependentTagForElementInView(document, element, view))
            {
                return BlockedChange(operation, index, "Element " + elementId + " already has an independent tag in view " + viewId + ".");
            }

            XYZ position;
            TagOrientation orientation;
            try
            {
                position = ToInternalPoint(positionValue, "position");
                orientation = ParseIndependentTagOrientation(GetString(operation, "orientation"));
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            bool hasLeader = GetBool(operation, "hasLeader", false);
            string probeError = ProbeIndependentTagCreation(document, element, view, tagType, position, hasLeader, orientation);
            if (!string.IsNullOrWhiteSpace(probeError)) return BlockedChange(operation, index, probeError);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["element"] = ElementSummary(document, element),
                    ["view"] = BuildViewSummary(view),
                    ["tagType"] = ElementSummary(document, tagType)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["elementId"] = ToElementIdString(element.Id),
                    ["viewId"] = ToElementIdString(view.Id),
                    ["tagTypeId"] = ToElementIdString(tagType.Id),
                    ["tagTypeName"] = SafeElementName(tagType),
                    ["position"] = PointValue(position),
                    ["hasLeader"] = hasLeader,
                    ["orientation"] = orientation.ToString()
                });
        }

        private static Dictionary<string, object> ApplyTagElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewTagElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "tag_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            FamilySymbol tagType = ResolveIndependentTagType(document, GetString(operation, "tagTypeId"));
            XYZ position = ToInternalPoint(GetDictionary(operation, "position"), "position");
            TagOrientation orientation = ParseIndependentTagOrientation(GetString(operation, "orientation"));

            if (!tagType.IsActive)
            {
                tagType.Activate();
                document.Regenerate();
            }

            Reference reference = new Reference(element);
            IndependentTag tag = IndependentTag.Create(
                document,
                tagType.Id,
                view.Id,
                reference,
                GetBool(operation, "hasLeader", false),
                orientation,
                position);
            if (tag == null)
            {
                throw new InvalidOperationException("Revit did not create an independent tag.");
            }

            document.Regenerate();
            return Change(operation, index, "applied",
                target: ElementTarget(tag, null),
                before: null,
                after: IndependentTagSnapshot(document, tag));
        }

        private static bool TryBuildFamilyPlacementRequest(
            Document document,
            Dictionary<string, object> operation,
            out FamilyPlacementRequest placement,
            out string error)
        {
            placement = null;
            error = null;

            string familySymbolId = GetString(operation, "familySymbolId");
            if (string.IsNullOrWhiteSpace(familySymbolId))
            {
                error = "place_family_instance requires familySymbolId.";
                return false;
            }

            FamilySymbol symbol = ResolveElement(document, familySymbolId) as FamilySymbol;
            if (symbol == null)
            {
                error = "FamilySymbol " + familySymbolId + " was not found.";
                return false;
            }

            if (symbol.Family == null)
            {
                error = "FamilySymbol " + familySymbolId + " does not expose a parent family.";
                return false;
            }

            Dictionary<string, object> locationValue = GetDictionary(operation, "location");
            if (locationValue == null)
            {
                error = "place_family_instance requires location.";
                return false;
            }

            string hostElementId = GetString(operation, "hostElementId");
            string levelId = GetString(operation, "levelId");
            bool hasHost = !string.IsNullOrWhiteSpace(hostElementId);
            bool allowPinnedHost = GetBool(operation, "allowPinnedHost", false);
            bool flipFacing = GetBool(operation, "flipFacing", false) || GetBool(operation, "facingFlipped", false);
            bool flipHand = GetBool(operation, "flipHand", false) || GetBool(operation, "handFlipped", false);

            double rotationRadians = 0;
            bool hasRotation = false;
            Dictionary<string, object> rotationValue = GetDictionary(operation, "rotation") ?? GetDictionary(operation, "angle");
            if (rotationValue != null)
            {
                try
                {
                    rotationRadians = ToInternalAngle(rotationValue);
                    if (!IsFinite(rotationRadians))
                    {
                        error = "place_family_instance rotation must be a finite angle.";
                        return false;
                    }

                    hasRotation = true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            FamilyPlacementType placementType = symbol.Family.FamilyPlacementType;
            if (IsWallHostedDoorWindowCategory(symbol))
            {
                if (placementType != FamilyPlacementType.OneLevelBasedHosted)
                {
                    error = "FamilySymbol " + familySymbolId + " is a door/window symbol but has unsupported placementType " + placementType + ". Expected OneLevelBasedHosted.";
                    return false;
                }

                if (!hasHost)
                {
                    error = "place_family_instance requires hostElementId for wall-hosted door/window symbols.";
                    return false;
                }

                Element host = ResolveElement(document, hostElementId);
                Wall hostWall = host as Wall;
                if (hostWall == null)
                {
                    error = "hostElementId " + hostElementId + " must reference a Wall for wall-hosted door/window placement.";
                    return false;
                }

                error = ValidateExpectedUniqueId(operation, hostWall, hostElementId, "expectedHostUniqueId", "Host element");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return false;
                }

                string hostError = ValidateWallFamilyInstanceHost(document, hostWall);
                if (!string.IsNullOrWhiteSpace(hostError))
                {
                    error = hostError;
                    return false;
                }

                if (hostWall.Pinned && !allowPinnedHost)
                {
                    error = "Host wall " + hostElementId + " is pinned. Pass allowPinnedHost=true only after explicitly reviewing the host modification.";
                    return false;
                }

                Level level = null;
                string levelSource = null;
                if (!string.IsNullOrWhiteSpace(levelId))
                {
                    level = ResolveElement(document, levelId) as Level;
                    if (level == null)
                    {
                        error = "Level " + levelId + " was not found.";
                        return false;
                    }

                    levelSource = "explicit";
                }
                else
                {
                    level = ResolveHostLevel(document, hostWall);
                    levelSource = level == null ? null : "host";
                }

                XYZ location;
                try
                {
                    location = ToInternalPlacementPoint(locationValue, "location", level?.Elevation);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }

                placement = new FamilyPlacementRequest(
                    "wallHosted",
                    symbol,
                    placementType,
                    hostWall,
                    level,
                    levelSource,
                    location,
                    hasRotation,
                    rotationRadians,
                    flipFacing,
                    flipHand,
                    allowPinnedHost);
                return true;
            }

            if (IsLevelBasedFurnitureEquipmentFixtureCategory(symbol))
            {
                if (!string.IsNullOrWhiteSpace(GetString(operation, "expectedHostUniqueId")))
                {
                    error = "expectedHostUniqueId requires hostElementId and is only supported for hosted place_family_instance operations.";
                    return false;
                }

                if (placementType != FamilyPlacementType.OneLevelBased)
                {
                    error = "FamilySymbol " + familySymbolId + " has placementType " + placementType + ". Level-based furniture/equipment/fixture placement requires OneLevelBased.";
                    return false;
                }

                if (hasHost)
                {
                    error = "hostElementId is only supported for wall-hosted door/window placement in place_family_instance.";
                    return false;
                }

                if (flipFacing || flipHand)
                {
                    error = "flipFacing and flipHand are only supported for wall-hosted door/window placement.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(levelId))
                {
                    error = "place_family_instance requires levelId for level-based furniture/equipment/fixture symbols.";
                    return false;
                }

                Level level = ResolveElement(document, levelId) as Level;
                if (level == null)
                {
                    error = "Level " + levelId + " was not found.";
                    return false;
                }

                XYZ location;
                try
                {
                    location = ToInternalPlacementPoint(locationValue, "location", level.Elevation);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }

                placement = new FamilyPlacementRequest(
                    "levelBased",
                    symbol,
                    placementType,
                    null,
                    level,
                    "explicit",
                    location,
                    hasRotation,
                    rotationRadians,
                    false,
                    false,
                    allowPinnedHost);
                return true;
            }

            string categoryName = symbol.Category == null ? "(none)" : symbol.Category.Name;
            error = "FamilySymbol " + familySymbolId + " category '" + categoryName + "' is not supported by place_family_instance. First supported cases are wall-hosted doors/windows and level-based furniture/equipment/fixtures.";
            return false;
        }

        private static void ApplyFamilyInstancePostPlacementTransforms(Document document, FamilyInstance instance, FamilyPlacementRequest placement)
        {
            if (placement.HasRotation && Math.Abs(placement.RotationRadians) > 0.000000001)
            {
                Line axis = Line.CreateBound(placement.Location, placement.Location + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(document, instance.Id, axis, placement.RotationRadians);
                instance = document.GetElement(instance.Id) as FamilyInstance ?? instance;
            }

            if (placement.FlipFacing)
            {
                if (!instance.CanFlipFacing)
                {
                    throw new InvalidOperationException("Created family instance does not support flipFacing.");
                }

                instance.flipFacing();
            }

            if (placement.FlipHand)
            {
                if (!instance.CanFlipHand)
                {
                    throw new InvalidOperationException("Created family instance does not support flipHand.");
                }

                instance.flipHand();
            }
        }

        private static Dictionary<string, object> FamilyPlacementTarget(Document document, FamilyPlacementRequest placement)
        {
            var target = new Dictionary<string, object>
            {
                ["document"] = document.Title,
                ["placementMode"] = placement.PlacementMode,
                ["familySymbol"] = ElementSummary(document, placement.Symbol)
            };

            if (placement.Level != null)
            {
                target["level"] = BuildLevelSummary(placement.Level);
                if (!string.IsNullOrWhiteSpace(placement.LevelSource)) target["levelSource"] = placement.LevelSource;
            }

            if (placement.Host != null)
            {
                Dictionary<string, object> host = ElementSummary(document, placement.Host);
                host["pinned"] = placement.Host.Pinned;
                target["host"] = host;
            }

            return target;
        }

        private static Dictionary<string, object> FamilyPlacementPreviewSnapshot(FamilyPlacementRequest placement)
        {
            var after = new Dictionary<string, object>
            {
                ["familySymbolId"] = ToElementIdString(placement.Symbol.Id),
                ["familySymbolName"] = SafeElementName(placement.Symbol),
                ["familyName"] = GetFamilyName(placement.Symbol),
                ["placementType"] = placement.PlacementType.ToString(),
                ["placementMode"] = placement.PlacementMode,
                ["location"] = PointValue(placement.Location),
                ["activationRequired"] = !placement.Symbol.IsActive,
                ["structuralType"] = StructuralType.NonStructural.ToString()
            };

            string builtInCategory = GetBuiltInCategoryName(placement.Symbol);
            if (!string.IsNullOrWhiteSpace(builtInCategory)) after["builtInCategory"] = builtInCategory;
            if (placement.Symbol.Category != null) after["category"] = placement.Symbol.Category.Name;

            if (placement.Level != null)
            {
                after["levelId"] = ToElementIdString(placement.Level.Id);
                after["levelName"] = placement.Level.Name;
                if (!string.IsNullOrWhiteSpace(placement.LevelSource)) after["levelSource"] = placement.LevelSource;
            }

            if (placement.Host != null)
            {
                after["hostElementId"] = ToElementIdString(placement.Host.Id);
                after["hostName"] = SafeElementName(placement.Host);
                after["hostPinned"] = placement.Host.Pinned;
                after["allowPinnedHost"] = placement.AllowPinnedHost;
            }

            if (placement.HasRotation) after["rotation"] = AngleValue(placement.RotationRadians);
            if (placement.FlipFacing) after["flipFacing"] = true;
            if (placement.FlipHand) after["flipHand"] = true;

            return after;
        }

        private static Dictionary<string, object> PreviewMoveElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> translationValue = GetDictionary(operation, "translation");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "move_element requires elementId.");
            if (translationValue == null) return BlockedChange(operation, index, "move_element requires translation.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be moved.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot be moved.");

            XYZ translation;
            try
            {
                translation = ToInternalPoint(translationValue, "translation");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (VectorLength(translation) <= 0) return BlockedChange(operation, index, "move_element translation must be non-zero.");

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation)
                });
        }

        private static Dictionary<string, object> ApplyMoveElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewMoveElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "move_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = LocationSnapshot(element);
            XYZ translation = ToInternalPoint(GetDictionary(operation, "translation"), "translation");
            ElementTransformUtils.MoveElement(document, element.Id, translation);
            Element movedElement = document.GetElement(element.Id);

            return Change(operation, index, "applied", ElementTarget(movedElement, null),
                before: before,
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation),
                    ["location"] = LocationSnapshot(movedElement)
                });
        }

        private static Dictionary<string, object> PreviewRotateElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> axisStartValue = GetDictionary(operation, "axisStart");
            Dictionary<string, object> axisEndValue = GetDictionary(operation, "axisEnd");
            Dictionary<string, object> angleValue = GetDictionary(operation, "angle");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "rotate_element requires elementId.");
            if (axisStartValue == null) return BlockedChange(operation, index, "rotate_element requires axisStart.");
            if (axisEndValue == null) return BlockedChange(operation, index, "rotate_element requires axisEnd.");
            if (angleValue == null) return BlockedChange(operation, index, "rotate_element requires angle.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be rotated.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot be rotated.");

            XYZ axisStart;
            XYZ axisEnd;
            double angleRadians;
            try
            {
                axisStart = ToInternalPoint(axisStartValue, "axisStart");
                axisEnd = ToInternalPoint(axisEndValue, "axisEnd");
                angleRadians = ToInternalAngle(angleValue);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (axisStart.DistanceTo(axisEnd) <= Math.Max(document.Application.ShortCurveTolerance, 0.000001))
            {
                return BlockedChange(operation, index, "rotate_element axisStart and axisEnd must define a non-zero axis.");
            }
            if (Math.Abs(angleRadians) <= 0.000000001)
            {
                return BlockedChange(operation, index, "rotate_element angle must be non-zero.");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["axisStart"] = PointValue(axisStart),
                    ["axisEnd"] = PointValue(axisEnd),
                    ["angle"] = AngleValue(angleRadians)
                });
        }

        private static Dictionary<string, object> ApplyRotateElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewRotateElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "rotate_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = LocationSnapshot(element);
            XYZ axisStart = ToInternalPoint(GetDictionary(operation, "axisStart"), "axisStart");
            XYZ axisEnd = ToInternalPoint(GetDictionary(operation, "axisEnd"), "axisEnd");
            double angleRadians = ToInternalAngle(GetDictionary(operation, "angle"));
            ElementTransformUtils.RotateElement(document, element.Id, Line.CreateBound(axisStart, axisEnd), angleRadians);
            Element rotatedElement = document.GetElement(element.Id);

            return Change(operation, index, "applied", ElementTarget(rotatedElement, null),
                before: before,
                after: new Dictionary<string, object>
                {
                    ["axisStart"] = PointValue(axisStart),
                    ["axisEnd"] = PointValue(axisEnd),
                    ["angle"] = AngleValue(angleRadians),
                    ["location"] = LocationSnapshot(rotatedElement)
                });
        }

        private static Dictionary<string, object> PreviewCopyElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> translationValue = GetDictionary(operation, "translation");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "copy_element requires elementId.");
            if (translationValue == null) return BlockedChange(operation, index, "copy_element requires translation.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be copied.");
            if (element.ViewSpecific) return BlockedChange(operation, index, "Element " + elementId + " is view-specific and cannot be copied by copy_element.");

            XYZ translation;
            try
            {
                translation = ToInternalPoint(translationValue, "translation");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (VectorLength(translation) <= 0) return BlockedChange(operation, index, "copy_element translation must be non-zero.");

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["source"] = ElementSummary(document, element)
                },
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation)
                });
        }

        private static Dictionary<string, object> ApplyCopyElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCopyElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "copy_element preview failed.");
            }

            Element source = ResolveElement(document, GetString(operation, "elementId"));
            XYZ translation = ToInternalPoint(GetDictionary(operation, "translation"), "translation");
            ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElement(document, source.Id, translation);
            if (copiedIds == null || copiedIds.Count == 0)
            {
                throw new InvalidOperationException("copy_element did not return any copied element ids.");
            }

            Element[] copiedElements = copiedIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null)
                .ToArray();

            return Change(operation, index, "applied",
                target: new Dictionary<string, object>
                {
                    ["source"] = ElementSummary(document, source)
                },
                before: LocationSnapshot(source),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation),
                    ["copiedElementIds"] = copiedIds.Select(ToElementIdString).ToArray(),
                    ["copiedElements"] = copiedElements.Select(element => ElementSummary(document, element)).ToArray()
                });
        }

        private static Dictionary<string, object> PreviewChangeElementType(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string typeId = GetString(operation, "typeId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "change_element_type requires elementId.");
            if (string.IsNullOrWhiteSpace(typeId)) return BlockedChange(operation, index, "change_element_type requires typeId.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is already an element type and cannot change type.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot change type.");

            ElementType targetType = ResolveElement(document, typeId) as ElementType;
            if (targetType == null) return BlockedChange(operation, index, "Type " + typeId + " was not found.");

            ElementId currentTypeId = element.GetTypeId();
            if (!IsValidElementId(currentTypeId)) return BlockedChange(operation, index, "Element " + elementId + " does not expose a valid type id.");
            if (string.Equals(ToElementIdString(currentTypeId), ToElementIdString(targetType.Id), StringComparison.Ordinal))
            {
                return BlockedChange(operation, index, "Element " + elementId + " already has type " + typeId + ".");
            }
            if (!IsValidTypeForElement(element, targetType.Id))
            {
                return BlockedChange(operation, index, "Type " + typeId + " is not valid for element " + elementId + ".");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: TypeSnapshot(document, element),
                after: TypeSnapshot(document, targetType));
        }

        private static Dictionary<string, object> ApplyChangeElementType(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewChangeElementType(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "change_element_type preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = TypeSnapshot(document, element);
            ElementType targetType = ResolveElement(document, GetString(operation, "typeId")) as ElementType;
            ElementId changedElementId = element.ChangeTypeId(targetType.Id);
            Element changedElement = IsValidElementId(changedElementId) ? document.GetElement(changedElementId) : null;
            if (changedElement == null) changedElement = document.GetElement(element.Id) ?? element;

            return Change(operation, index, "applied", ElementTarget(changedElement, null),
                before: before,
                after: TypeSnapshot(document, changedElement));
        }

        private static Dictionary<string, object> PreviewSetElementPinned(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "set_element_pinned requires elementId.");
            if (!operation.TryGetValue("pinned", out object pinnedValue) || pinnedValue == null)
            {
                return BlockedChange(operation, index, "set_element_pinned requires pinned.");
            }

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be pinned or unpinned.");

            bool desiredPinned = Convert.ToBoolean(pinnedValue, CultureInfo.InvariantCulture);
            bool? expectedPinned = GetNullableBool(operation, "expectedPinned");
            if (expectedPinned.HasValue && expectedPinned.Value != element.Pinned)
            {
                return BlockedChange(
                    operation,
                    index,
                    "Element " + elementId + " pinned state is " + element.Pinned.ToString(CultureInfo.InvariantCulture) +
                    " but expectedPinned was " + expectedPinned.Value.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: new Dictionary<string, object>
                {
                    ["pinned"] = element.Pinned
                },
                after: new Dictionary<string, object>
                {
                    ["pinned"] = desiredPinned
                });
        }

        private static Dictionary<string, object> ApplySetElementPinned(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewSetElementPinned(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "set_element_pinned preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            bool before = element.Pinned;
            bool desiredPinned = GetBool(operation, "pinned", false);
            element.Pinned = desiredPinned;

            return Change(operation, index, "applied", ElementTarget(element, null),
                before: new Dictionary<string, object>
                {
                    ["pinned"] = before
                },
                after: new Dictionary<string, object>
                {
                    ["pinned"] = element.Pinned
                });
        }

        private static Dictionary<string, object> PreviewDeleteElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "delete_element requires elementId.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be deleted by delete_element.");

            string expectedUniqueId = GetString(operation, "expectedUniqueId");
            if (!string.IsNullOrWhiteSpace(expectedUniqueId) && !string.Equals(element.UniqueId, expectedUniqueId, StringComparison.Ordinal))
            {
                return BlockedChange(operation, index, "Element " + elementId + " uniqueId did not match expectedUniqueId.");
            }

            bool? expectedPinned = GetNullableBool(operation, "expectedPinned");
            if (expectedPinned.HasValue && expectedPinned.Value != element.Pinned)
            {
                return BlockedChange(
                    operation,
                    index,
                    "Element " + elementId + " pinned state is " + element.Pinned.ToString(CultureInfo.InvariantCulture) +
                    " but expectedPinned was " + expectedPinned.Value.ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (element.Pinned && !GetBool(operation, "allowPinned", false))
            {
                return BlockedChange(operation, index, "Element " + elementId + " is pinned. Pass allowPinned=true only after explicitly reviewing the target.");
            }

            ElementId targetId = element.Id;
            Dictionary<string, object> target = ElementTarget(element, null);
            Dictionary<string, object> before = DeleteSnapshot(document, element);
            DeleteProbeResult probe;
            try
            {
                probe = ProbeDeleteElement(document, element);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, "Revit could not preview delete_element for " + elementId + ": " + FormatPreviewProbeError("delete_element", ex));
            }

            Dictionary<string, object> after = DeleteAfterSnapshot(probe.DeletedIds, targetId);
            string guardFailure = ValidateDeleteProbeGuards(operation, probe);
            if (!string.IsNullOrWhiteSpace(guardFailure))
            {
                return Change(operation, index, "blocked", target, before, after, guardFailure);
            }

            return Change(operation, index, "ready", target,
                before: before,
                after: after);
        }

        private static Dictionary<string, object> ApplyDeleteElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewDeleteElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "delete_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            ElementId targetId = element.Id;
            Dictionary<string, object> target = ElementTarget(element, null);
            Dictionary<string, object> before = DeleteSnapshot(document, element);
            ICollection<ElementId> deletedIds = document.Delete(targetId);

            return Change(operation, index, "applied", target,
                before: before,
                after: DeleteAfterSnapshot(deletedIds ?? Array.Empty<ElementId>(), targetId));
        }

        private static string ValidateDeleteProbeGuards(Dictionary<string, object> operation, DeleteProbeResult probe)
        {
            int deletedCount = probe.DeletedIds.Count;
            int dependentCount = probe.DependentIds.Count;
            bool allowDependentDeletes = GetBool(operation, "allowDependentDeletes", false);
            IReadOnlyList<string> expectedDeletedElementIds = GetStringList(operation, "expectedDeletedElementIds");
            int? expectedDeletedCount = GetInt(operation, "expectedDeletedCount");
            int dependentDeleteLimit = Math.Min(
                MaxDeleteDependentLimit,
                Math.Max(1, GetInt(operation, "dependentDeleteLimit") ?? DefaultDeleteDependentLimit));

            if (expectedDeletedCount.HasValue && expectedDeletedCount.Value != deletedCount)
            {
                return "delete_element would delete " + deletedCount.ToString(CultureInfo.InvariantCulture) +
                    " element(s), but expectedDeletedCount was " + expectedDeletedCount.Value.ToString(CultureInfo.InvariantCulture) + ".";
            }

            if (expectedDeletedElementIds.Count > 0)
            {
                var expected = new HashSet<string>(expectedDeletedElementIds, StringComparer.OrdinalIgnoreCase);
                var actual = new HashSet<string>(probe.DeletedIds.Select(ToElementIdString), StringComparer.OrdinalIgnoreCase);
                if (expected.Count != actual.Count || !expected.SetEquals(actual))
                {
                    return "delete_element expectedDeletedElementIds did not match Revit's delete set. Actual deletedElementIds: " +
                        FormatElementIdList(actual) + ".";
                }
            }

            if (deletedCount > dependentDeleteLimit && !allowDependentDeletes && expectedDeletedElementIds.Count == 0)
            {
                return "delete_element would delete " + deletedCount.ToString(CultureInfo.InvariantCulture) +
                    " element(s), above dependentDeleteLimit " + dependentDeleteLimit.ToString(CultureInfo.InvariantCulture) +
                    ". Pass allowDependentDeletes=true or exact expectedDeletedElementIds after reviewing the preview.";
            }

            if (dependentCount > 0 && !allowDependentDeletes && expectedDeletedElementIds.Count == 0)
            {
                return "delete_element would also delete " + dependentCount.ToString(CultureInfo.InvariantCulture) +
                    " dependent element(s): " + FormatElementIdList(probe.DependentIds.Select(ToElementIdString)) +
                    ". Pass allowDependentDeletes=true or exact expectedDeletedElementIds after reviewing the preview.";
            }

            return null;
        }

        private static DeleteProbeResult ProbeDeleteElement(Document document, Element element)
        {
            ElementId targetId = element.Id;
            if (document.IsModifiable)
            {
                using (var subTransaction = new SubTransaction(document))
                {
                    bool started = false;
                    try
                    {
                        if (subTransaction.Start() != TransactionStatus.Started)
                        {
                            throw new InvalidOperationException("Could not start Revit subtransaction for delete preview.");
                        }
                        started = true;
                        ICollection<ElementId> deletedIds = document.Delete(targetId);
                        return new DeleteProbeResult(deletedIds ?? Array.Empty<ElementId>(), targetId);
                    }
                    finally
                    {
                        RollBackPreviewProbeSubTransaction(subTransaction, started);
                    }
                }
            }

            using (var transaction = new Transaction(document, "Revit MCP preview delete_element"))
            {
                TransactionStatus startStatus = transaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("Could not start Revit transaction for delete preview: " + startStatus);
                }

                ConfigurePreviewProbeTransaction(transaction);
                try
                {
                    ICollection<ElementId> deletedIds = document.Delete(targetId);
                    return new DeleteProbeResult(deletedIds ?? Array.Empty<ElementId>(), targetId);
                }
                finally
                {
                    RollBackPreviewProbeTransaction(transaction);
                }
            }
        }

        private static Dictionary<string, object> DeleteAfterSnapshot(IEnumerable<ElementId> deletedIds, ElementId targetId)
        {
            List<ElementId> ids = (deletedIds ?? Array.Empty<ElementId>()).ToList();
            string targetIdString = ToElementIdString(targetId);
            List<ElementId> dependentIds = ids
                .Where(id => !string.Equals(ToElementIdString(id), targetIdString, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var after = new Dictionary<string, object>
            {
                ["deleted"] = true,
                ["deletedCount"] = ids.Count,
                ["deletedElementIds"] = ids.Select(ToElementIdString).ToArray(),
                ["dependentDeletedCount"] = dependentIds.Count
            };

            if (dependentIds.Count > 0)
            {
                after["dependentDeletedElementIds"] = dependentIds.Select(ToElementIdString).ToArray();
            }

            return after;
        }

        private sealed class DeleteProbeResult
        {
            public DeleteProbeResult(IEnumerable<ElementId> deletedIds, ElementId targetId)
            {
                DeletedIds = (deletedIds ?? Array.Empty<ElementId>()).ToList();
                string targetIdString = ToElementIdString(targetId);
                DependentIds = DeletedIds
                    .Where(id => !string.Equals(ToElementIdString(id), targetIdString, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            public List<ElementId> DeletedIds { get; }
            public List<ElementId> DependentIds { get; }
        }

        private static Dictionary<string, object> Change(
            Dictionary<string, object> operation,
            int index,
            string status,
            Dictionary<string, object> target,
            Dictionary<string, object> before,
            Dictionary<string, object> after,
            string message = null)
        {
            var change = new Dictionary<string, object>
            {
                ["operationIndex"] = index,
                ["type"] = GetString(operation, "type") ?? "unknown",
                ["status"] = status
            };

            string operationId = GetString(operation, "id");
            if (!string.IsNullOrWhiteSpace(operationId)) change["operationId"] = operationId;
            if (target != null) change["target"] = target;
            if (before != null) change["before"] = before;
            if (after != null) change["after"] = after;
            if (!string.IsNullOrWhiteSpace(message)) change["message"] = message;
            return change;
        }

        private static Dictionary<string, object> BlockedChange(Dictionary<string, object> operation, int index, string message)
        {
            return Change(operation, index, "blocked", null, null, null, message);
        }

        private static string ValidateExpectedUniqueId(
            Dictionary<string, object> operation,
            Element element,
            string elementId,
            string fieldName = "expectedUniqueId",
            string label = "Element")
        {
            string expectedUniqueId = GetString(operation, fieldName);
            if (string.IsNullOrWhiteSpace(expectedUniqueId)) return null;

            if (!string.Equals(element.UniqueId, expectedUniqueId, StringComparison.Ordinal))
            {
                return label + " " + elementId + " uniqueId did not match " + fieldName + ".";
            }

            return null;
        }

        private sealed class PreviewValidationContext
        {
            private readonly HashSet<string> _levelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _gridNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _roomNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _sheetNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool TryAddLevelName(string name)
            {
                return _levelNames.Add((name ?? string.Empty).Trim());
            }

            public bool TryAddGridName(string name)
            {
                return _gridNames.Add((name ?? string.Empty).Trim());
            }

            public bool TryAddRoomNumber(string number)
            {
                return _roomNumbers.Add((number ?? string.Empty).Trim());
            }

            public bool TryAddSheetNumber(string number)
            {
                return _sheetNumbers.Add((number ?? string.Empty).Trim());
            }
        }

        private sealed class FamilyLoadRequest
        {
            public FamilyLoadRequest(
                string fullPath,
                string fileName,
                string familyName,
                long fileSizeBytes,
                string fileSha256,
                string expectedSha256,
                IReadOnlyList<string> allowedCategories,
                bool overwriteParameterValues,
                bool allowNetworkPath)
            {
                FullPath = fullPath;
                FileName = fileName;
                FamilyName = familyName;
                FileSizeBytes = fileSizeBytes;
                FileSha256 = fileSha256;
                ExpectedSha256 = expectedSha256;
                AllowedCategories = allowedCategories ?? Array.Empty<string>();
                OverwriteParameterValues = overwriteParameterValues;
                AllowNetworkPath = allowNetworkPath;
            }

            public string FullPath { get; }
            public string FileName { get; }
            public string FamilyName { get; }
            public long FileSizeBytes { get; }
            public string FileSha256 { get; }
            public string ExpectedSha256 { get; }
            public IReadOnlyList<string> AllowedCategories { get; }
            public bool OverwriteParameterValues { get; }
            public bool AllowNetworkPath { get; }
        }

        private sealed class ControlledFamilyLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwriteParameterValues;

            public ControlledFamilyLoadOptions(bool overwriteParameterValues)
            {
                _overwriteParameterValues = overwriteParameterValues;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwriteParameterValues;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwriteParameterValues;
                return true;
            }
        }

        private sealed class PreviewProbeFailurePreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                bool hasError = false;
                foreach (FailureMessageAccessor failure in failures)
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(failure);
                        continue;
                    }

                    hasError = true;
                }

                return hasError
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue;
            }
        }

        private sealed class FamilyPlacementRequest
        {
            public FamilyPlacementRequest(
                string placementMode,
                FamilySymbol symbol,
                FamilyPlacementType placementType,
                Element host,
                Level level,
                string levelSource,
                XYZ location,
                bool hasRotation,
                double rotationRadians,
                bool flipFacing,
                bool flipHand,
                bool allowPinnedHost)
            {
                PlacementMode = placementMode;
                Symbol = symbol;
                PlacementType = placementType;
                Host = host;
                Level = level;
                LevelSource = levelSource;
                Location = location;
                HasRotation = hasRotation;
                RotationRadians = rotationRadians;
                FlipFacing = flipFacing;
                FlipHand = flipHand;
                AllowPinnedHost = allowPinnedHost;
            }

            public string PlacementMode { get; }
            public FamilySymbol Symbol { get; }
            public FamilyPlacementType PlacementType { get; }
            public Element Host { get; }
            public Level Level { get; }
            public string LevelSource { get; }
            public XYZ Location { get; }
            public bool HasRotation { get; }
            public double RotationRadians { get; }
            public bool FlipFacing { get; }
            public bool FlipHand { get; }
            public bool AllowPinnedHost { get; }
        }

        private static Dictionary<string, object> ElementTarget(Element element, string parameterName)
        {
            var target = new Dictionary<string, object>
            {
                ["elementId"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };
            if (element.Category != null) target["category"] = element.Category.Name;
            if (!string.IsNullOrWhiteSpace(parameterName)) target["parameterName"] = parameterName;
            return target;
        }

        private static Dictionary<string, object> ParameterSnapshot(Parameter parameter)
        {
            return new Dictionary<string, object>
            {
                ["value"] = ParameterValue(parameter),
                ["storageType"] = parameter.StorageType.ToString(),
                ["isReadOnly"] = parameter.IsReadOnly
            };
        }

        private static void SetParameterValue(Parameter parameter, object value)
        {
            if (parameter.IsReadOnly) throw new InvalidOperationException("Parameter is read-only.");

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    parameter.Set(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    break;
                case StorageType.Integer:
                    if (value is bool booleanValue)
                    {
                        parameter.Set(booleanValue ? 1 : 0);
                    }
                    else
                    {
                        parameter.Set(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    }
                    break;
                case StorageType.Double:
                    parameter.Set(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                case StorageType.ElementId:
                    parameter.Set(CreateElementId(Convert.ToString(value, CultureInfo.InvariantCulture)));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported parameter storage type: " + parameter.StorageType);
            }
        }

        private static Element ResolveElement(Document document, string elementId)
        {
            try
            {
                return document.GetElement(CreateElementId(elementId));
            }
            catch
            {
                return null;
            }
        }

        private static bool LevelNameExists(Document document, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Any(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool GridNameExists(Document document, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Any(grid => string.Equals(grid.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool RoomNumberExists(Document document, string number)
        {
            string normalized = NormalizeOptionalText(number);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            return new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Any(room => string.Equals(GetRoomNumber(room), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SheetNumberExists(Document document, string number)
        {
            string normalized = NormalizeOptionalText(number);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            return new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Any(sheet => string.Equals(sheet.SheetNumber, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static FloorType ResolveFloorType(Document document, string floorTypeId)
        {
            if (!string.IsNullOrWhiteSpace(floorTypeId))
            {
                return ResolveElement(document, floorTypeId) as FloorType;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault();
        }

        private static FamilySymbol ResolveTitleBlockType(Document document, string titleBlockTypeId)
        {
            if (string.IsNullOrWhiteSpace(titleBlockTypeId)) return null;

            FamilySymbol symbol = ResolveElement(document, titleBlockTypeId) as FamilySymbol;
            if (symbol == null) return null;
            return string.Equals(GetBuiltInCategoryName(symbol), "OST_TitleBlocks", StringComparison.OrdinalIgnoreCase) ? symbol : null;
        }

        private static TextNoteType ResolveTextNoteType(Document document, string textNoteTypeId)
        {
            if (!string.IsNullOrWhiteSpace(textNoteTypeId))
            {
                return ResolveElement(document, textNoteTypeId) as TextNoteType;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault();
        }

        private static Element ResolveRoomTagTypeElement(Document document, string tagTypeId)
        {
            if (!string.IsNullOrWhiteSpace(tagTypeId))
            {
                Element element = ResolveElement(document, tagTypeId);
                return IsRoomTagTypeElement(element) ? element : null;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .Cast<Element>()
                .FirstOrDefault();
        }

        private static FamilySymbol ResolveIndependentTagType(Document document, string tagTypeId)
        {
            if (string.IsNullOrWhiteSpace(tagTypeId)) return null;

            FamilySymbol symbol = ResolveElement(document, tagTypeId) as FamilySymbol;
            if (symbol == null) return null;
            if (!IsTagFamilySymbol(symbol)) return null;
            if (IsMaterialTagFamilySymbol(symbol)) return null;
            return symbol;
        }

        private static string ValidateTextNoteView(View view)
        {
            if (view == null) return "Target view was not found.";
            if (view.IsTemplate) return "Target view is a template and cannot host text notes.";
            if (view.ViewType == ViewType.ThreeD) return "3D views are not supported by create_text_note.";
            if (view is ViewSheet) return null;
            return IsGraphicalView(view) ? null : "Target view must be a graphical printable view or sheet.";
        }

        private static string ValidateRoomTagView(View view)
        {
            if (view == null) return "Target view was not found.";
            if (view.IsTemplate) return "Target view is a template and cannot host room tags.";
            if (view is ViewSheet) return "Sheets cannot host room tags.";
            if (!IsGraphicalView(view)) return "Target view must be a graphical printable view.";
            if (view.ViewType == ViewType.FloorPlan ||
                view.ViewType == ViewType.CeilingPlan ||
                view.ViewType == ViewType.EngineeringPlan ||
                view.ViewType == ViewType.Section)
            {
                return null;
            }

            return "Room tags require a plan or section view.";
        }

        private static string ValidateIndependentTagView(View view)
        {
            if (view == null) return "Target view was not found.";
            if (view.IsTemplate) return "Target view is a template and cannot host element tags.";
            if (view is ViewSheet) return "Sheets cannot host element tags.";
            if (view.ViewType == ViewType.ThreeD) return "3D views are not supported by tag_element.";
            return IsGraphicalView(view) ? null : "Target view must be a graphical printable view.";
        }

        private static string ValidateIndependentTagTarget(Element element)
        {
            if (element is ElementType) return "tag_element target must be a model instance, not an element type.";
            if (element is View) return "tag_element cannot target views or sheets.";
            if (element is Room) return "Use tag_room for rooms.";
            string className = element.GetType().Name;
            if (string.Equals(className, "Area", StringComparison.OrdinalIgnoreCase)) return "Area tags are not supported by tag_element yet.";
            if (string.Equals(className, "Space", StringComparison.OrdinalIgnoreCase)) return "Space tags are not supported by tag_element yet.";
            if (element.Category == null) return "tag_element target must expose a category.";
            return null;
        }

        private static SpatialElementTagOrientation ParseRoomTagOrientation(string orientation)
        {
            if (string.IsNullOrWhiteSpace(orientation)) return SpatialElementTagOrientation.Horizontal;
            if (string.Equals(orientation, "Horizontal", StringComparison.OrdinalIgnoreCase)) return SpatialElementTagOrientation.Horizontal;
            if (string.Equals(orientation, "Vertical", StringComparison.OrdinalIgnoreCase)) return SpatialElementTagOrientation.Vertical;
            if (string.Equals(orientation, "Model", StringComparison.OrdinalIgnoreCase)) return SpatialElementTagOrientation.Model;
            throw new ArgumentException("Unsupported room tag orientation: " + orientation + ".");
        }

        private static TagOrientation ParseIndependentTagOrientation(string orientation)
        {
            if (string.IsNullOrWhiteSpace(orientation)) return TagOrientation.Horizontal;
            if (string.Equals(orientation, "Horizontal", StringComparison.OrdinalIgnoreCase)) return TagOrientation.Horizontal;
            if (string.Equals(orientation, "Vertical", StringComparison.OrdinalIgnoreCase)) return TagOrientation.Vertical;
            if (string.Equals(orientation, "AnyModelDirection", StringComparison.OrdinalIgnoreCase)) return TagOrientation.AnyModelDirection;
            throw new ArgumentException("Unsupported element tag orientation: " + orientation + ".");
        }

        private static bool HasRoomTagInView(Document document, Room room, View view)
        {
            try
            {
                return new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(RoomTag))
                    .Cast<RoomTag>()
                    .Any(tag =>
                    {
                        try
                        {
                            if (tag.TaggedLocalRoomId == room.Id) return true;
                            Room taggedRoom = tag.Room;
                            return taggedRoom != null && taggedRoom.Id == room.Id;
                        }
                        catch
                        {
                            return false;
                        }
                    });
            }
            catch
            {
                return false;
            }
        }

        private static string ProbeRoomTagCreation(
            Document document,
            Room room,
            View view,
            Element tagType,
            UV location,
            bool hasLeader,
            SpatialElementTagOrientation orientation)
        {
            if (document.IsModifiable) return null;

            using (var transaction = new Transaction(document, "Revit MCP preview tag_room"))
            {
                TransactionStatus startStatus = transaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    return "Revit could not start a room-tag preview transaction. Status: " + startStatus + ".";
                }

                ConfigurePreviewProbeTransaction(transaction);
                try
                {
                    RoomTag tag = document.Create.NewRoomTag(new LinkElementId(room.Id), location, view.Id);
                    if (tag == null) return "Revit did not create a room tag during preview validation.";
                    if (tagType != null) tag.ChangeTypeId(tagType.Id);
                    tag.HasLeader = hasLeader;
                    tag.TagOrientation = orientation;
                    document.Regenerate();
                    return null;
                }
                catch (Exception ex)
                {
                    return FormatPreviewProbeError("tag_room", ex);
                }
                finally
                {
                    RollBackPreviewProbeTransaction(transaction);
                }
            }
        }

        private static string ProbeIndependentTagCreation(
            Document document,
            Element element,
            View view,
            FamilySymbol tagType,
            XYZ position,
            bool hasLeader,
            TagOrientation orientation)
        {
            if (document.IsModifiable) return null;

            using (var transaction = new Transaction(document, "Revit MCP preview tag_element"))
            {
                TransactionStatus startStatus = transaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    return "Revit could not start an element-tag preview transaction. Status: " + startStatus + ".";
                }

                ConfigurePreviewProbeTransaction(transaction);
                try
                {
                    if (!tagType.IsActive)
                    {
                        tagType.Activate();
                        document.Regenerate();
                    }

                    IndependentTag tag = IndependentTag.Create(
                        document,
                        tagType.Id,
                        view.Id,
                        new Reference(element),
                        hasLeader,
                        orientation,
                        position);
                    if (tag == null) return "Revit did not create an element tag during preview validation.";
                    document.Regenerate();
                    return null;
                }
                catch (Exception ex)
                {
                    return FormatPreviewProbeError("tag_element", ex);
                }
                finally
                {
                    RollBackPreviewProbeTransaction(transaction);
                }
            }
        }

        private static void ConfigurePreviewProbeTransaction(Transaction transaction)
        {
            var preprocessor = new PreviewProbeFailurePreprocessor();
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            options.SetFailuresPreprocessor(preprocessor);
            transaction.SetFailureHandlingOptions(options);
        }

        private static void RollBackPreviewProbeTransaction(Transaction transaction)
        {
            try
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                {
                    transaction.RollBack();
                }
            }
            catch
            {
                // Preview probes are best-effort validation. If Revit is already unwinding
                // a failed transaction, preserve the original preview error.
            }
        }

        private static void RollBackPreviewProbeSubTransaction(SubTransaction subTransaction, bool started)
        {
            if (!started) return;
            try
            {
                subTransaction.RollBack();
            }
            catch
            {
                // Preserve the original preview error if Revit already unwound the subtransaction.
            }
        }

        private static string FormatPreviewProbeError(string operationType, Exception exception)
        {
            string message = exception == null ? string.Empty : exception.Message;
            if (string.IsNullOrWhiteSpace(message)) message = exception == null ? "Unknown Revit API error." : exception.GetType().Name;
            message = message.Replace("\r", " ").Replace("\n", " ").Trim();
            return "Revit rejected " + operationType + " preview: " + message;
        }

        private static string FormatElementIdList(IEnumerable<string> ids, int max = 12)
        {
            List<string> values = (ids ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Take(max + 1)
                .ToList();
            if (values.Count <= max) return string.Join(", ", values);
            values = values.Take(max).ToList();
            return string.Join(", ", values) + ", ...";
        }

        private static bool HasIndependentTagForElementInView(Document document, Element element, View view)
        {
            try
            {
                return new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .Any(tag =>
                    {
                        try
                        {
                            return tag.GetTaggedLocalElementIds().Any(id => id == element.Id);
                        }
                        catch
                        {
                            return false;
                        }
                    });
            }
            catch
            {
                return false;
            }
        }

        private static bool IsElementVisibleInView(Document document, Element element, View view)
        {
            try
            {
                return new FilteredElementCollector(document, view.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Any(id => id == element.Id);
            }
            catch
            {
                return true;
            }
        }

        private static Level ResolveHostLevel(Document document, Element host)
        {
            if (host == null) return null;

            ElementId levelId = GetLevelId(host);
            if (IsValidElementId(levelId))
            {
                Level level = document.GetElement(levelId) as Level;
                if (level != null) return level;
            }

            try
            {
                Parameter baseConstraint = host.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                ElementId baseLevelId = baseConstraint?.AsElementId();
                if (IsValidElementId(baseLevelId))
                {
                    return document.GetElement(baseLevelId) as Level;
                }
            }
            catch
            {
                // Non-wall hosts or unusual wall variants may not expose a base constraint.
            }

            return null;
        }

        private static double ToInternalElevation(Dictionary<string, object> elevation)
        {
            return ToInternalLength(elevation, "Elevation");
        }

        private static double ToInternalLength(Dictionary<string, object> unitValue, string fieldName)
        {
            if (unitValue == null) throw new ArgumentException(fieldName + " is required.");
            double value = GetDouble(unitValue, "value") ?? throw new ArgumentException(fieldName + " value is required.");
            string unit = (GetString(unitValue, "unit") ?? "mm").Trim().ToLowerInvariant();

            switch (unit)
            {
                case "mm":
                case "millimeters":
                    return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
                case "m":
                case "meters":
                    return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
                case "ft":
                case "feet":
                case "revit-internal":
                    return value;
                default:
                    throw new ArgumentException("Unsupported " + fieldName.ToLowerInvariant() + " unit: " + unit);
            }
        }

        private static double ToInternalAngle(Dictionary<string, object> angleValue)
        {
            if (angleValue == null) throw new ArgumentException("Angle is required.");
            double value = GetDouble(angleValue, "value") ?? throw new ArgumentException("Angle value is required.");
            string unit = (GetString(angleValue, "unit") ?? "degrees").Trim().ToLowerInvariant();

            switch (unit)
            {
                case "degrees":
                    return value * Math.PI / 180.0;
                case "radians":
                    return value;
                default:
                    throw new ArgumentException("Unsupported angle unit: " + unit);
            }
        }

        private static XYZ ToInternalPoint(Dictionary<string, object> point, string fieldName)
        {
            if (point == null) throw new ArgumentException(fieldName + " is required.");
            return new XYZ(
                ToInternalLength(GetDictionary(point, "x"), fieldName + ".x"),
                ToInternalLength(GetDictionary(point, "y"), fieldName + ".y"),
                ToInternalLength(GetDictionary(point, "z"), fieldName + ".z"));
        }

        private static XYZ ToInternalPlacementPoint(Dictionary<string, object> point, string fieldName, double? defaultZ)
        {
            if (point == null) throw new ArgumentException(fieldName + " is required.");

            double z;
            Dictionary<string, object> zValue = GetDictionary(point, "z");
            if (zValue != null)
            {
                z = ToInternalLength(zValue, fieldName + ".z");
            }
            else if (defaultZ.HasValue)
            {
                z = defaultZ.Value;
            }
            else
            {
                throw new ArgumentException(fieldName + ".z is required when no level elevation can be inferred.");
            }

            return new XYZ(
                ToInternalLength(GetDictionary(point, "x"), fieldName + ".x"),
                ToInternalLength(GetDictionary(point, "y"), fieldName + ".y"),
                z);
        }

        private static UV ToInternalUv(Dictionary<string, object> point, string fieldName)
        {
            if (point == null) throw new ArgumentException(fieldName + " is required.");
            return new UV(
                ToInternalLength(GetDictionary(point, "x"), fieldName + ".x"),
                ToInternalLength(GetDictionary(point, "y"), fieldName + ".y"));
        }

        private static XYZ ToInternalSheetPoint(Dictionary<string, object> point, string fieldName)
        {
            UV uv = ToInternalUv(point, fieldName);
            return new XYZ(uv.U, uv.V, 0);
        }

        private static List<XYZ> ToInternalPointList(IReadOnlyList<Dictionary<string, object>> points, string fieldName)
        {
            var result = new List<XYZ>();
            for (int index = 0; index < points.Count; index++)
            {
                result.Add(ToInternalPoint(points[index], fieldName + "[" + index.ToString(CultureInfo.InvariantCulture) + "]"));
            }

            return result;
        }

        private static string ValidateWallBaseline(Document document, XYZ start, XYZ end)
        {
            if (Math.Abs(start.Z - end.Z) > 0.000001)
            {
                return "create_wall start and end must have the same z elevation.";
            }

            double length = start.DistanceTo(end);
            double minimumLength = Math.Max(document.Application.ShortCurveTolerance, 0.000001);
            if (length <= minimumLength)
            {
                return "create_wall baseline is shorter than Revit's minimum curve length.";
            }

            return null;
        }

        private static string ValidateWallFamilyInstanceHost(Document document, Wall wall)
        {
            if (wall == null) return "hostElementId must reference a Wall.";

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve?.Curve == null)
            {
                return "Host wall " + ToElementIdString(wall.Id) + " does not expose a valid location curve.";
            }

            WallType wallType = document.GetElement(wall.GetTypeId()) as WallType;
            if (wallType != null && wallType.Kind == WallKind.Curtain)
            {
                return "Curtain wall hosts are not supported by place_family_instance for door/window family placement.";
            }

            return null;
        }

        private static string ValidateLinearDatum(Document document, XYZ start, XYZ end, string operationName)
        {
            if (Math.Abs(start.Z - end.Z) > 0.000001)
            {
                return operationName + " start and end must have the same z elevation.";
            }

            double length = start.DistanceTo(end);
            double minimumLength = Math.Max(document.Application.ShortCurveTolerance, 0.000001);
            if (length <= minimumLength)
            {
                return operationName + " line is shorter than Revit's minimum curve length.";
            }

            return null;
        }

        private static string ValidateFloorOutline(Document document, Level level, List<XYZ> rawPoints)
        {
            List<XYZ> points = NormalizeClosedLoop(rawPoints);
            if (points.Count < 3)
            {
                return "create_floor requires at least three unique outline points.";
            }

            double tolerance = Math.Max(document.Application.ShortCurveTolerance, 0.000001);
            for (int index = 0; index < points.Count; index++)
            {
                XYZ current = points[index];
                if (Math.Abs(current.Z - level.Elevation) > 0.000001)
                {
                    return "create_floor outline points must be on the target level elevation.";
                }

                XYZ next = points[(index + 1) % points.Count];
                if (current.DistanceTo(next) <= tolerance)
                {
                    return "create_floor outline has a segment shorter than Revit's minimum curve length.";
                }
            }

            double area = PolygonAreaInternal(points);
            if (area <= tolerance * tolerance)
            {
                return "create_floor outline area is too small.";
            }

            return null;
        }

        private static List<XYZ> NormalizeClosedLoop(List<XYZ> points)
        {
            var result = new List<XYZ>(points ?? new List<XYZ>());
            if (result.Count > 1 && result[0].DistanceTo(result[result.Count - 1]) <= 0.000001)
            {
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private static CurveLoop BuildCurveLoop(IReadOnlyList<XYZ> points)
        {
            var loop = new CurveLoop();
            for (int index = 0; index < points.Count; index++)
            {
                loop.Append(Line.CreateBound(points[index], points[(index + 1) % points.Count]));
            }

            return loop;
        }

        private static double PolygonAreaInternal(IReadOnlyList<XYZ> points)
        {
            if (points == null || points.Count < 3) return 0;

            double signedArea = 0;
            for (int index = 0; index < points.Count; index++)
            {
                XYZ current = points[index];
                XYZ next = points[(index + 1) % points.Count];
                signedArea += (current.X * next.Y) - (next.X * current.Y);
            }

            return Math.Abs(signedArea) / 2.0;
        }

        private static double VectorLength(XYZ vector)
        {
            return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static Dictionary<string, object> LengthValue(double internalLength)
        {
            return UnitValue(UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Millimeters), "mm", "metric");
        }

        private static Dictionary<string, object> AreaValue(double internalArea)
        {
            double squareMeters = UnitUtils.ConvertFromInternalUnits(internalArea, UnitTypeId.SquareMeters);
            return UnitValue(squareMeters, "m2", "metric");
        }

        private static Dictionary<string, object> VolumeValue(double internalVolume)
        {
            double cubicMeters = UnitUtils.ConvertFromInternalUnits(internalVolume, UnitTypeId.CubicMeters);
            return UnitValue(cubicMeters, "m3", "metric");
        }

        private static Dictionary<string, object> AngleValue(double radians)
        {
            return new Dictionary<string, object>
            {
                ["value"] = Math.Round(radians * 180.0 / Math.PI, 6),
                ["unit"] = "degrees",
                ["radians"] = Math.Round(radians, 9)
            };
        }

        private static Dictionary<string, object> PointValue(XYZ point)
        {
            return new Dictionary<string, object>
            {
                ["x"] = LengthValue(point.X),
                ["y"] = LengthValue(point.Y),
                ["z"] = LengthValue(point.Z)
            };
        }

        private static Dictionary<string, object> Point2Value(UV point)
        {
            return new Dictionary<string, object>
            {
                ["x"] = LengthValue(point.U),
                ["y"] = LengthValue(point.V)
            };
        }

        private static object[] PointArrayValue(IEnumerable<XYZ> points)
        {
            return points.Select(PointValue).ToArray();
        }

        private static void SetWallDoubleParameter(Wall wall, BuiltInParameter builtInParameter, double value, string parameterName)
        {
            Parameter parameter = wall.get_Parameter(builtInParameter);
            if (parameter == null) throw new InvalidOperationException("Wall " + parameterName + " parameter was not found.");
            if (parameter.IsReadOnly) throw new InvalidOperationException("Wall " + parameterName + " parameter is read-only.");
            parameter.Set(value);
        }

        private static void SetRoomStringParameter(Room room, BuiltInParameter builtInParameter, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            Parameter parameter = room.get_Parameter(builtInParameter);
            if (parameter == null) throw new InvalidOperationException("Room parameter " + builtInParameter + " was not found.");
            if (parameter.IsReadOnly) throw new InvalidOperationException("Room parameter " + builtInParameter + " is read-only.");
            parameter.Set(value);
        }

        private static string NormalizeOptionalText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static Dictionary<string, object> ElementSummary(Document document, Element element)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };

            if (element.Category != null) summary["category"] = element.Category.Name;

            ElementId typeId = element.GetTypeId();
            if (IsValidElementId(typeId)) summary["typeId"] = ToElementIdString(typeId);

            ElementId levelId = GetLevelId(element);
            if (IsValidElementId(levelId)) summary["levelId"] = ToElementIdString(levelId);

            ElementType elementType = element as ElementType;
            if (elementType != null && !string.IsNullOrWhiteSpace(elementType.FamilyName))
            {
                summary["familyName"] = elementType.FamilyName;
            }

            return summary;
        }

        private static Dictionary<string, object> SheetSnapshot(Document document, ViewSheet sheet)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(sheet.Id),
                ["uniqueId"] = sheet.UniqueId,
                ["sheetNumber"] = sheet.SheetNumber,
                ["name"] = SafeElementName(sheet),
                ["titleBlockIds"] = GetSheetTitleBlockIds(document, sheet).ToArray()
            };

            return snapshot;
        }

        private static Dictionary<string, object> ViewportSnapshot(Document document, Viewport viewport)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(viewport.Id),
                ["uniqueId"] = viewport.UniqueId,
                ["viewId"] = ToElementIdString(viewport.ViewId)
            };

            try
            {
                snapshot["sheetId"] = ToElementIdString(viewport.SheetId);
            }
            catch
            {
                // Older/unusual viewport variants may not expose SheetId reliably.
            }

            View view = document.GetElement(viewport.ViewId) as View;
            if (view != null)
            {
                snapshot["viewName"] = SafeElementName(view);
                snapshot["viewType"] = view.ViewType.ToString();
            }

            try
            {
                snapshot["center"] = PointValue(viewport.GetBoxCenter());
            }
            catch
            {
                // Viewport box center can be unavailable for unusual sheet contents.
            }

            return snapshot;
        }

        private static Dictionary<string, object> TextNoteSnapshot(Document document, TextNote textNote)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(textNote.Id),
                ["uniqueId"] = textNote.UniqueId,
                ["text"] = textNote.Text,
                ["textLength"] = (textNote.Text ?? string.Empty).Length,
                ["viewId"] = ToElementIdString(textNote.OwnerViewId)
            };

            ElementId typeId = textNote.GetTypeId();
            if (IsValidElementId(typeId))
            {
                snapshot["textNoteTypeId"] = ToElementIdString(typeId);
                Element type = document.GetElement(typeId);
                if (type != null) snapshot["textNoteTypeName"] = SafeElementName(type);
            }

            try
            {
                snapshot["position"] = PointValue(textNote.Coord);
            }
            catch
            {
                // Some text note states do not expose a stable coordinate.
            }

            try
            {
                if (textNote.Width > 0) snapshot["width"] = LengthValue(textNote.Width);
            }
            catch
            {
                // Unwrapped text notes may not expose a width.
            }

            return snapshot;
        }

        private static Dictionary<string, object> RoomTagSnapshot(Document document, RoomTag tag)
        {
            var snapshot = ElementSummary(document, tag);
            snapshot["viewId"] = ToElementIdString(tag.OwnerViewId);

            try
            {
                if (IsValidElementId(tag.TaggedLocalRoomId)) snapshot["roomId"] = ToElementIdString(tag.TaggedLocalRoomId);
            }
            catch
            {
                // Linked or orphaned room tag states may not expose a local room id.
            }

            try
            {
                Room room = tag.Room;
                if (room != null)
                {
                    snapshot["roomNumber"] = GetRoomNumber(room);
                    snapshot["roomName"] = GetRoomName(room);
                }
            }
            catch
            {
                // Orphaned or unusual tags may not expose the tagged room.
            }

            try
            {
                ElementId typeId = tag.GetTypeId();
                if (IsValidElementId(typeId))
                {
                    snapshot["tagTypeId"] = ToElementIdString(typeId);
                    Element tagType = document.GetElement(typeId);
                    if (tagType != null) snapshot["tagTypeName"] = SafeElementName(tagType);
                }
            }
            catch
            {
                // Some tag states may not expose their type id.
            }

            try
            {
                snapshot["position"] = PointValue(tag.TagHeadPosition);
            }
            catch
            {
                // Tag head position can be unavailable for some orphaned states.
            }

            try
            {
                snapshot["hasLeader"] = tag.HasLeader;
            }
            catch
            {
                // Ignore leader state when unavailable.
            }

            try
            {
                snapshot["orientation"] = tag.TagOrientation.ToString();
            }
            catch
            {
                // Ignore orientation when unavailable.
            }

            try
            {
                string tagText = tag.TagText;
                if (!string.IsNullOrWhiteSpace(tagText)) snapshot["tagText"] = tagText;
            }
            catch
            {
                // Ignore tag text when unavailable.
            }

            return snapshot;
        }

        private static Dictionary<string, object> IndependentTagSnapshot(Document document, IndependentTag tag)
        {
            var snapshot = ElementSummary(document, tag);
            snapshot["viewId"] = ToElementIdString(tag.OwnerViewId);

            ElementId typeId = tag.GetTypeId();
            if (IsValidElementId(typeId))
            {
                snapshot["tagTypeId"] = ToElementIdString(typeId);
                Element type = document.GetElement(typeId);
                if (type != null) snapshot["tagTypeName"] = SafeElementName(type);
            }

            try
            {
                string[] localIds = tag.GetTaggedLocalElementIds()
                    .Where(IsValidElementId)
                    .Select(ToElementIdString)
                    .ToArray();
                if (localIds.Length > 0) snapshot["taggedElementIds"] = localIds;
            }
            catch
            {
                // Some independent tag variants may not expose local tagged ids.
            }

            try
            {
                snapshot["position"] = PointValue(tag.TagHeadPosition);
            }
            catch
            {
                // Tag head position can be unavailable for some orphaned states.
            }

            try
            {
                snapshot["hasLeader"] = tag.HasLeader;
            }
            catch
            {
                // Some tag behavior variants do not expose leader state.
            }

            try
            {
                snapshot["orientation"] = tag.TagOrientation.ToString();
            }
            catch
            {
                // Some tag behavior variants do not expose orientation.
            }

            try
            {
                snapshot["isMulticategoryTag"] = tag.IsMulticategoryTag;
                snapshot["isMaterialTag"] = tag.IsMaterialTag;
            }
            catch
            {
                // Ignore variant flags when unavailable.
            }

            try
            {
                string tagText = tag.TagText;
                if (!string.IsNullOrWhiteSpace(tagText)) snapshot["tagText"] = tagText;
            }
            catch
            {
                // Ignore tag text when unavailable.
            }

            return snapshot;
        }

        private static Dictionary<string, object> TypeSnapshot(Document document, Element element)
        {
            ElementType elementType = element as ElementType;
            if (elementType != null) return ElementSummary(document, elementType);

            ElementId typeId = element?.GetTypeId();
            if (!IsValidElementId(typeId))
            {
                return new Dictionary<string, object>
                {
                    ["available"] = false
                };
            }

            ElementType resolvedType = document.GetElement(typeId) as ElementType;
            if (resolvedType == null)
            {
                return new Dictionary<string, object>
                {
                    ["typeId"] = ToElementIdString(typeId),
                    ["available"] = false
                };
            }

            return ElementSummary(document, resolvedType);
        }

        private static bool IsValidTypeForElement(Element element, ElementId typeId)
        {
            try
            {
                ICollection<ElementId> validTypeIds = element.GetValidTypes();
                if (validTypeIds == null || validTypeIds.Count == 0) return true;
                return validTypeIds.Any(candidate => string.Equals(ToElementIdString(candidate), ToElementIdString(typeId), StringComparison.Ordinal));
            }
            catch
            {
                return true;
            }
        }

        private static Dictionary<string, object> WallSnapshot(Wall wall)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(wall.Id),
                ["uniqueId"] = wall.UniqueId,
                ["name"] = SafeElementName(wall),
                ["typeId"] = ToElementIdString(wall.GetTypeId()),
                ["levelId"] = ToElementIdString(GetLevelId(wall)),
                ["flipped"] = wall.Flipped
            };

            Parameter height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (height != null && height.StorageType == StorageType.Double) snapshot["height"] = LengthValue(height.AsDouble());

            Parameter baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            if (baseOffset != null && baseOffset.StorageType == StorageType.Double) snapshot["baseOffset"] = LengthValue(baseOffset.AsDouble());

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve?.Curve != null && locationCurve.Curve.IsBound)
            {
                snapshot["start"] = PointValue(locationCurve.Curve.GetEndPoint(0));
                snapshot["end"] = PointValue(locationCurve.Curve.GetEndPoint(1));
                snapshot["length"] = LengthValue(locationCurve.Curve.Length);
            }

            return snapshot;
        }

        private static Dictionary<string, object> GridSnapshot(Grid grid)
        {
            var snapshot = ElementSummary(grid.Document, grid);
            Curve curve = grid.Curve;
            if (curve != null && curve.IsBound)
            {
                snapshot["start"] = PointValue(curve.GetEndPoint(0));
                snapshot["end"] = PointValue(curve.GetEndPoint(1));
                snapshot["length"] = LengthValue(curve.Length);
            }

            return snapshot;
        }

        private static Dictionary<string, object> FloorSnapshot(Floor floor, IReadOnlyList<XYZ> outline)
        {
            var snapshot = ElementSummary(floor.Document, floor);
            ElementId levelId = GetLevelId(floor);
            if (IsValidElementId(levelId)) snapshot["levelId"] = ToElementIdString(levelId);
            if (outline != null && outline.Count > 0)
            {
                snapshot["outline"] = PointArrayValue(outline);
                snapshot["area"] = AreaValue(PolygonAreaInternal(outline));
            }

            Parameter structural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
            if (structural != null && structural.StorageType == StorageType.Integer)
            {
                snapshot["structural"] = structural.AsInteger() != 0;
            }

            return snapshot;
        }

        private static Dictionary<string, object> RoomSnapshot(Room room)
        {
            var snapshot = ElementSummary(room.Document, room);
            string number = GetRoomNumber(room);
            if (!string.IsNullOrWhiteSpace(number)) snapshot["number"] = number;
            snapshot["name"] = GetRoomName(room);

            ElementId levelId = GetLevelId(room);
            if (IsValidElementId(levelId))
            {
                snapshot["levelId"] = ToElementIdString(levelId);
                Element level = room.Document.GetElement(levelId);
                if (level != null) snapshot["levelName"] = SafeElementName(level);
            }

            ElementId phaseId = GetCreatedPhaseId(room);
            if (IsValidElementId(phaseId))
            {
                snapshot["phaseId"] = ToElementIdString(phaseId);
                Element phase = room.Document.GetElement(phaseId);
                if (phase != null) snapshot["phaseName"] = SafeElementName(phase);
            }

            double area = SafeRoomArea(room);
            double volume = SafeRoomVolume(room);
            if (area > 0) snapshot["area"] = AreaValue(area);
            if (volume > 0) snapshot["volume"] = VolumeValue(volume);

            Parameter perimeter = room.get_Parameter(BuiltInParameter.ROOM_PERIMETER);
            if (perimeter != null && perimeter.StorageType == StorageType.Double)
            {
                double perimeterValue = perimeter.AsDouble();
                if (perimeterValue > 0) snapshot["perimeter"] = LengthValue(perimeterValue);
            }

            LocationPoint point = room.Location as LocationPoint;
            if (point != null) snapshot["location"] = PointValue(point.Point);

            snapshot["isPlaced"] = IsRoomPlaced(room);
            snapshot["isEnclosed"] = area > 0;

            string department = GetRoomDepartment(room);
            if (!string.IsNullOrWhiteSpace(department)) snapshot["department"] = department;

            return snapshot;
        }

        private static Dictionary<string, object> FamilyInstanceSnapshot(FamilyInstance instance)
        {
            var snapshot = ElementSummary(instance.Document, instance);
            snapshot["pinned"] = instance.Pinned;

            FamilySymbol symbol = instance.Symbol;
            if (symbol != null)
            {
                snapshot["familySymbolId"] = ToElementIdString(symbol.Id);
                snapshot["familySymbolName"] = SafeElementName(symbol);
                snapshot["familyName"] = GetFamilyName(symbol);
                string placementType = GetPlacementType(symbol);
                if (!string.IsNullOrWhiteSpace(placementType)) snapshot["placementType"] = placementType;
                snapshot["symbolIsActive"] = symbol.IsActive;

                string builtInCategory = GetBuiltInCategoryName(symbol);
                if (!string.IsNullOrWhiteSpace(builtInCategory)) snapshot["builtInCategory"] = builtInCategory;
            }

            ElementId levelId = GetLevelId(instance);
            if (IsValidElementId(levelId))
            {
                snapshot["levelId"] = ToElementIdString(levelId);
                Element level = instance.Document.GetElement(levelId);
                if (level != null) snapshot["levelName"] = SafeElementName(level);
            }

            Element host = null;
            try
            {
                host = instance.Host;
            }
            catch
            {
                host = null;
            }

            if (host != null)
            {
                snapshot["hostElementId"] = ToElementIdString(host.Id);
                snapshot["hostCategory"] = host.Category?.Name;
                snapshot["host"] = ElementSummary(instance.Document, host);
            }

            Dictionary<string, object> location = LocationSnapshot(instance);
            if (location != null) snapshot["location"] = location;

            TryAddFamilyInstanceBool(snapshot, instance, "CanFlipFacing");
            TryAddFamilyInstanceBool(snapshot, instance, "FacingFlipped");
            TryAddFamilyInstanceBool(snapshot, instance, "CanFlipHand");
            TryAddFamilyInstanceBool(snapshot, instance, "HandFlipped");

            try
            {
                snapshot["facingOrientation"] = PointValue(instance.FacingOrientation);
            }
            catch
            {
                // Optional family instance metadata varies by placement type.
            }

            try
            {
                snapshot["handOrientation"] = PointValue(instance.HandOrientation);
            }
            catch
            {
                // Optional family instance metadata varies by placement type.
            }

            return snapshot;
        }

        private static void TryAddFamilyInstanceBool(Dictionary<string, object> snapshot, FamilyInstance instance, string propertyName)
        {
            try
            {
                System.Reflection.PropertyInfo property = instance.GetType().GetProperty(propertyName);
                if (property != null && property.GetValue(instance, null) is bool value)
                {
                    snapshot[char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1)] = value;
                }
            }
            catch
            {
                // Optional family instance metadata varies by placement type.
            }
        }

        private static Dictionary<string, object> DeleteSnapshot(Document document, Element element)
        {
            var snapshot = ElementSummary(document, element);
            snapshot["pinned"] = element.Pinned;
            snapshot["viewSpecific"] = element.ViewSpecific;
            return snapshot;
        }

        private static Dictionary<string, object> LocationSnapshot(Element element)
        {
            if (element == null) return null;

            LocationPoint point = element.Location as LocationPoint;
            if (point != null)
            {
                return new Dictionary<string, object>
                {
                    ["point"] = PointValue(point.Point),
                    ["rotation"] = Math.Round(point.Rotation, 6)
                };
            }

            LocationCurve curve = element.Location as LocationCurve;
            if (curve?.Curve != null && curve.Curve.IsBound)
            {
                return new Dictionary<string, object>
                {
                    ["start"] = PointValue(curve.Curve.GetEndPoint(0)),
                    ["end"] = PointValue(curve.Curve.GetEndPoint(1)),
                    ["length"] = LengthValue(curve.Curve.Length)
                };
            }

            return BoundsSnapshot(element) ?? UnavailableGeometrySnapshot();
        }

        private static Dictionary<string, object> BoundsSnapshot(Element element)
        {
            if (element == null) return null;

            try
            {
                return BoundsValue(element.get_BoundingBox(null));
            }
            catch
            {
                return UnavailableGeometrySnapshot();
            }
        }

        private static Dictionary<string, object> BoundsValue(BoundingBoxXYZ boundingBox)
        {
            if (boundingBox?.Min == null || boundingBox.Max == null) return UnavailableGeometrySnapshot();

            XYZ min = boundingBox.Min;
            XYZ max = boundingBox.Max;
            Transform transform = boundingBox.Transform;
            XYZ[] corners =
            {
                TransformBoundingBoxPoint(transform, new XYZ(min.X, min.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, min.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(min.X, max.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(min.X, min.Y, max.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, max.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, min.Y, max.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(min.X, max.Y, max.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, max.Y, max.Z))
            };

            return new Dictionary<string, object>
            {
                ["min"] = PointValue(new XYZ(corners.Min(point => point.X), corners.Min(point => point.Y), corners.Min(point => point.Z))),
                ["max"] = PointValue(new XYZ(corners.Max(point => point.X), corners.Max(point => point.Y), corners.Max(point => point.Z)))
            };
        }

        private static XYZ TransformBoundingBoxPoint(Transform transform, XYZ point)
        {
            return transform == null ? point : transform.OfPoint(point);
        }

        private static Dictionary<string, object> UnavailableGeometrySnapshot()
        {
            return new Dictionary<string, object>
            {
                ["available"] = false
            };
        }

        private static List<Dictionary<string, object>> GetOperations(Dictionary<string, object> payload)
        {
            var operations = new List<Dictionary<string, object>>();
            if (payload == null || !payload.TryGetValue("operations", out object value) || value == null) return operations;

            if (value is object[] array)
            {
                foreach (object item in array)
                {
                    if (item is Dictionary<string, object> operation) operations.Add(operation);
                }
            }
            else if (value is ArrayList list)
            {
                foreach (object item in list)
                {
                    if (item is Dictionary<string, object> operation) operations.Add(operation);
                }
            }

            return operations;
        }

        private static List<Dictionary<string, object>> GetPointList(Dictionary<string, object> payload, string key)
        {
            var points = new List<Dictionary<string, object>>();
            if (payload == null || !payload.TryGetValue(key, out object value) || value == null) return points;

            if (value is object[] array)
            {
                foreach (object item in array)
                {
                    if (item is Dictionary<string, object> point) points.Add(point);
                }
            }
            else if (value is ArrayList list)
            {
                foreach (object item in list)
                {
                    if (item is Dictionary<string, object> point) points.Add(point);
                }
            }

            return points;
        }

        private static string GetTransactionName(Dictionary<string, object> payload)
        {
            string name = GetString(payload, "transactionName");
            return string.IsNullOrWhiteSpace(name) ? "Revit MCP Next change" : name.Trim();
        }

        private static string ComputePreviewId(Document document, string transactionName, List<Dictionary<string, object>> operations)
        {
            string raw = ComputeDocumentFingerprint(document) + "|" + transactionName + "|" + Canonicalize(operations);
            return HashString(raw).Substring(0, 24);
        }

        private static string ComputeChangeSetHash(object value)
        {
            return "sha256:" + HashString(Canonicalize(value));
        }

        private static string ComputePreviewChangeSetHash(
            string documentFingerprint,
            long generation,
            string transactionName,
            string operationsHash,
            string changesHash)
        {
            return ComputeChangeSetHash(new Dictionary<string, object>
            {
                ["documentFingerprint"] = documentFingerprint,
                ["baseGeneration"] = generation,
                ["transactionName"] = transactionName,
                ["operationsHash"] = operationsHash,
                ["changesHash"] = changesHash
            });
        }

        private static string FormatUtc(DateTimeOffset value)
        {
            return value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        private static string Canonicalize(object value)
        {
            if (value == null) return "null";
            if (value is Dictionary<string, object> dictionary)
            {
                return "{" + string.Join(",", dictionary.Keys.OrderBy(key => key, StringComparer.Ordinal)
                    .Select(key => key + ":" + Canonicalize(dictionary[key]))) + "}";
            }
            if (value is IEnumerable<Dictionary<string, object>> dictionaryEnumerable)
            {
                return "[" + string.Join(",", dictionaryEnumerable.Select(Canonicalize)) + "]";
            }
            if (value is object[] array)
            {
                return "[" + string.Join(",", array.Select(Canonicalize)) + "]";
            }
            if (value is ArrayList list)
            {
                return "[" + string.Join(",", list.Cast<object>().Select(Canonicalize)) + "]";
            }
            if (value is bool boolean) return boolean ? "true" : "false";
            if (IsNumeric(value)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string HashString(string raw)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static Dictionary<string, object> BuildAddinAssemblyIdentity()
        {
            var identity = new Dictionary<string, object>();

            try
            {
                string assemblyPath = typeof(RevitExternalEventHandler).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    identity["assemblyPath"] = assemblyPath;
                    if (File.Exists(assemblyPath))
                    {
                        using (SHA256 sha = SHA256.Create())
                        using (FileStream stream = File.OpenRead(assemblyPath))
                        {
                            identity["assemblySha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                        }

                        FileVersionInfo version = FileVersionInfo.GetVersionInfo(assemblyPath);
                        if (!string.IsNullOrWhiteSpace(version.FileVersion)) identity["fileVersion"] = version.FileVersion;
                        if (!string.IsNullOrWhiteSpace(version.ProductVersion)) identity["productVersion"] = version.ProductVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                identity["assemblyIdentityError"] = ex.Message;
            }

            return identity;
        }

        private static Dictionary<string, object> BuildProjectInfoSummary(ProjectInfo projectInfo)
        {
            if (projectInfo == null) return new Dictionary<string, object>();
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(projectInfo.Id),
                ["uniqueId"] = projectInfo.UniqueId
            };

            AddIfNotBlank(summary, "number", projectInfo.Number);
            AddIfNotBlank(summary, "name", projectInfo.Name);
            AddIfNotBlank(summary, "clientName", projectInfo.ClientName);
            AddIfNotBlank(summary, "status", projectInfo.Status);
            AddIfNotBlank(summary, "issueDate", projectInfo.IssueDate);
            AddIfNotBlank(summary, "address", projectInfo.Address);
            AddIfNotBlank(summary, "buildingName", projectInfo.BuildingName);
            AddIfNotBlank(summary, "organizationName", projectInfo.OrganizationName);
            AddIfNotBlank(summary, "organizationDescription", projectInfo.OrganizationDescription);
            AddIfNotBlank(summary, "author", projectInfo.Author);
            return summary;
        }

        private static List<Dictionary<string, object>> BuildPhaseSummaries(Document document)
        {
            var phases = new List<Dictionary<string, object>>();
            int sequence = 0;
            foreach (Phase phase in document.Phases.Cast<Phase>())
            {
                phases.Add(new Dictionary<string, object>
                {
                    ["id"] = ToElementIdString(phase.Id),
                    ["name"] = phase.Name,
                    ["sequence"] = sequence++
                });
            }

            return phases;
        }

        private static Dictionary<string, object> BuildWorksetSection(Document document, int limit, bool includeTotalCount)
        {
            if (!document.IsWorkshared)
            {
                var unavailable = BuildContextSection(new List<Dictionary<string, object>>(), limit, includeTotalCount);
                unavailable["available"] = false;
                return unavailable;
            }

            var worksets = new FilteredWorksetCollector(document)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .OrderBy(workset => workset.Name, StringComparer.OrdinalIgnoreCase)
                .Select(BuildWorksetSummary)
                .ToList();
            var section = BuildContextSection(worksets, limit, includeTotalCount);
            section["available"] = true;
            return section;
        }

        private static Dictionary<string, object> BuildWorksetSummary(Workset workset)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToWorksetIdString(workset.Id),
                ["uniqueId"] = workset.UniqueId.ToString("D"),
                ["name"] = workset.Name,
                ["kind"] = workset.Kind.ToString(),
                ["isOpen"] = workset.IsOpen,
                ["isEditable"] = workset.IsEditable,
                ["isVisibleByDefault"] = workset.IsVisibleByDefault,
                ["isDefaultWorkset"] = workset.IsDefaultWorkset
            };
            AddIfNotBlank(summary, "owner", workset.Owner);
            return summary;
        }

        private static List<Dictionary<string, object>> BuildDesignOptionSummaries(Document document)
        {
            ElementId activeOptionId = null;
            try
            {
                activeOptionId = DesignOption.GetActiveDesignOptionId(document);
            }
            catch
            {
                activeOptionId = null;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>()
                .OrderBy(option => GetDesignOptionSetName(option), StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => SafeElementName(option), StringComparer.OrdinalIgnoreCase)
                .Select(option => BuildDesignOptionSummary(option, activeOptionId))
                .ToList();
        }

        private static Dictionary<string, object> BuildDesignOptionSummary(DesignOption option, ElementId activeOptionId)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(option.Id),
                ["uniqueId"] = option.UniqueId,
                ["name"] = SafeElementName(option),
                ["isPrimary"] = option.IsPrimary,
                ["isActive"] = IsValidElementId(activeOptionId) && string.Equals(ToElementIdString(activeOptionId), ToElementIdString(option.Id), StringComparison.OrdinalIgnoreCase)
            };

            AddIfNotBlank(summary, "optionSetId", GetDesignOptionSetId(option));
            AddIfNotBlank(summary, "optionSetName", GetDesignOptionSetName(option));
            return summary;
        }

        private static string GetDesignOptionSetId(DesignOption option)
        {
            try
            {
                ElementId id = option.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
                return IsValidElementId(id) ? ToElementIdString(id) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetDesignOptionSetName(DesignOption option)
        {
            try
            {
                Parameter parameter = option.get_Parameter(BuiltInParameter.OPTION_SET_NAME);
                return parameter?.AsString() ?? parameter?.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        private static List<Dictionary<string, object>> BuildRevitLinkSummaries(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .OrderBy(link => SafeElementName(link), StringComparer.OrdinalIgnoreCase)
                .Select(link => BuildRevitLinkSummary(document, link))
                .ToList();
        }

        private static Dictionary<string, object> BuildRevitLinkSummary(Document document, RevitLinkInstance link)
        {
            ElementId typeId = link.GetTypeId();
            RevitLinkType linkType = document.GetElement(typeId) as RevitLinkType;
            Document linkedDocument = null;
            try
            {
                linkedDocument = link.GetLinkDocument();
            }
            catch
            {
                linkedDocument = null;
            }

            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(link.Id),
                ["uniqueId"] = link.UniqueId,
                ["name"] = SafeElementName(link)
            };

            if (IsValidElementId(typeId)) summary["typeId"] = ToElementIdString(typeId);
            if (linkType != null)
            {
                AddIfNotBlank(summary, "typeName", SafeElementName(linkType));
                summary["isLoaded"] = IsRevitLinkLoaded(document, linkType);
                AddIfNotBlank(summary, "loadStatus", GetRevitLinkStatus(linkType));
            }
            else
            {
                summary["isLoaded"] = linkedDocument != null;
            }

            if (linkedDocument != null)
            {
                AddIfNotBlank(summary, "linkedDocumentTitle", linkedDocument.Title);
                AddIfNotBlank(summary, "linkedDocumentPath", linkedDocument.PathName);
            }

            return summary;
        }

        private static bool IsRevitLinkLoaded(Document document, RevitLinkType linkType)
        {
            try
            {
                return RevitLinkType.IsLoaded(document, linkType.Id);
            }
            catch
            {
                try
                {
                    return linkType.GetLinkedFileStatus() == LinkedFileStatus.Loaded;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string GetRevitLinkStatus(RevitLinkType linkType)
        {
            try
            {
                return linkType.GetLinkedFileStatus().ToString();
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> BuildContextSection(List<Dictionary<string, object>> items, int limit, bool includeTotalCount)
        {
            List<Dictionary<string, object>> page = items.Take(limit).ToList();
            var section = new Dictionary<string, object>
            {
                ["items"] = page.ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = page.Count < items.Count
            };
            if (includeTotalCount) section["totalCount"] = items.Count;
            return section;
        }

        private static void AddIfNotBlank(Dictionary<string, object> target, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
        }

        private static Dictionary<string, object> BuildModelReadiness(UIApplication app, Document document, long generation, Dictionary<string, object> payload)
        {
            IReadOnlyList<string> requestedScenarios = GetStringList(payload, "scenarios");
            HashSet<string> requestedScenarioSet = requestedScenarios.Count > 0
                ? new HashSet<string>(requestedScenarios, StringComparer.OrdinalIgnoreCase)
                : null;

            bool WantsScenario(string name)
            {
                return requestedScenarioSet == null || requestedScenarioSet.Contains(name);
            }

            Level[] levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToArray();

            View activeView = SafeActiveView(document);
            int? wallCount = null;
            int? wallTypeCount = null;
            int? floorTypeCount = null;
            int? roomCount = null;
            int? textNoteTypeCount = null;
            FamilySymbol[] familySymbols = null;
            FamilySymbol[] wallHostedSymbols = null;
            FamilySymbol[] levelBasedSymbols = null;

            int GetWallCount()
            {
                if (!wallCount.HasValue)
                {
                    wallCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType());
                }
                return wallCount.Value;
            }

            int GetWallTypeCount()
            {
                if (!wallTypeCount.HasValue)
                {
                    wallTypeCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfClass(typeof(WallType)));
                }
                return wallTypeCount.Value;
            }

            int GetFloorTypeCount()
            {
                if (!floorTypeCount.HasValue)
                {
                    floorTypeCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfClass(typeof(FloorType)));
                }
                return floorTypeCount.Value;
            }

            int GetRoomCount()
            {
                if (!roomCount.HasValue)
                {
                    roomCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType());
                }
                return roomCount.Value;
            }

            int GetTextNoteTypeCount()
            {
                if (!textNoteTypeCount.HasValue)
                {
                    textNoteTypeCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfClass(typeof(TextNoteType)));
                }
                return textNoteTypeCount.Value;
            }

            FamilySymbol[] GetFamilySymbols()
            {
                if (familySymbols == null)
                {
                    familySymbols = new FilteredElementCollector(document)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .ToArray();
                }
                return familySymbols;
            }

            FamilySymbol[] GetWallHostedSymbols()
            {
                if (wallHostedSymbols == null)
                {
                    wallHostedSymbols = GetFamilySymbols()
                        .Where(IsSupportedWallHostedFamilySymbol)
                        .ToArray();
                }
                return wallHostedSymbols;
            }

            FamilySymbol[] GetLevelBasedSymbols()
            {
                if (levelBasedSymbols == null)
                {
                    levelBasedSymbols = GetFamilySymbols()
                        .Where(IsSupportedLevelBasedFamilySymbol)
                        .ToArray();
                }
                return levelBasedSymbols;
            }

            var scenarios = new List<Dictionary<string, object>>();
            if (WantsScenario("levels"))
            {
                scenarios.Add(ScenarioReadiness(
                    "levels",
                    levels.Length > 0,
                    levels.Length > 0 ? Array.Empty<string>() : new[] { "At least one project level." },
                    levels.Length > 0 ? "Use revit.get_levels to pick exact level IDs." : "Create a level before level-based model operations.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["defaultLevelId"] = levels.FirstOrDefault() == null ? null : ToElementIdString(levels.First().Id) }));
            }
            if (WantsScenario("wallCreation"))
            {
                int wallCreationWallTypeCount = GetWallTypeCount();
                int wallCreationWallCount = GetWallCount();
                scenarios.Add(ScenarioReadiness(
                    "wallCreation",
                    levels.Length > 0 && wallCreationWallTypeCount > 0,
                    MissingPrerequisites(
                        levels.Length > 0 ? null : "At least one project level.",
                        wallCreationWallTypeCount > 0 ? null : "At least one wall type."),
                    "Use create_wall with levelId, start, end, and optional wallTypeId discovered from revit.catalog.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["wallTypeCount"] = wallCreationWallTypeCount, ["wallCount"] = wallCreationWallCount }));
            }
            if (WantsScenario("floorCreation"))
            {
                int floorCreationTypeCount = GetFloorTypeCount();
                scenarios.Add(ScenarioReadiness(
                    "floorCreation",
                    levels.Length > 0 && floorCreationTypeCount > 0,
                    MissingPrerequisites(
                        levels.Length > 0 ? null : "At least one project level.",
                        floorCreationTypeCount > 0 ? null : "At least one floor type."),
                    "Use create_floor with a closed outline on the target level elevation.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["floorTypeCount"] = floorCreationTypeCount }));
            }
            if (WantsScenario("roomCreation"))
            {
                int roomCreationWallCount = GetWallCount();
                int roomCreationRoomCount = GetRoomCount();
                scenarios.Add(ScenarioReadiness(
                    "roomCreation",
                    levels.Length > 0,
                    levels.Length > 0 ? Array.Empty<string>() : new[] { "At least one project level." },
                    roomCreationWallCount > 0
                        ? "Create or reuse an enclosed room-bounding region, then preview create_room."
                        : "Create room-bounding walls or separators before expecting room area and enclosure.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["roomCount"] = roomCreationRoomCount, ["wallCount"] = roomCreationWallCount }));
            }
            if (WantsScenario("roomReadback"))
            {
                int roomReadbackCount = GetRoomCount();
                scenarios.Add(ScenarioReadiness(
                    "roomReadback",
                    roomReadbackCount > 0,
                    roomReadbackCount > 0 ? Array.Empty<string>() : new[] { "At least one placed room." },
                    "Use revit.get_rooms with preset=schedule for compact room export.",
                    new Dictionary<string, object> { ["roomCount"] = roomReadbackCount }));
            }
            if (WantsScenario("typeChange"))
            {
                int typeChangeScanned;
                bool typeChangeScanTruncated;
                int typeChangeCandidateCount = CountTypeChangeCandidates(document, 250, out typeChangeScanned, out typeChangeScanTruncated);
                scenarios.Add(ScenarioReadiness(
                    "typeChange",
                    typeChangeCandidateCount > 0,
                    typeChangeCandidateCount > 0 ? Array.Empty<string>() : new[] { "A non-pinned model element with compatible alternate types." },
                    "Use revit.catalog with kind=elementTypes and filter.forElementId before change_element_type.",
                    new Dictionary<string, object> { ["sampledElements"] = typeChangeScanned, ["candidateElements"] = typeChangeCandidateCount, ["scanTruncated"] = typeChangeScanTruncated }));
            }
            if (WantsScenario("familyPlacement"))
            {
                int familyPlacementWallCount = GetWallCount();
                FamilySymbol[] familyPlacementWallHostedSymbols = GetWallHostedSymbols();
                FamilySymbol[] familyPlacementLevelBasedSymbols = GetLevelBasedSymbols();
                scenarios.Add(ScenarioReadiness(
                    "familyPlacement",
                    levels.Length > 0 && (familyPlacementLevelBasedSymbols.Length > 0 || (familyPlacementWallCount > 0 && familyPlacementWallHostedSymbols.Length > 0)),
                    MissingPrerequisites(
                        levels.Length > 0 ? null : "At least one project level.",
                        familyPlacementLevelBasedSymbols.Length > 0 || familyPlacementWallHostedSymbols.Length > 0 ? null : "At least one supported door/window/furniture/equipment/fixture FamilySymbol.",
                        familyPlacementWallHostedSymbols.Length == 0 || familyPlacementWallCount > 0 ? null : "At least one wall host for hosted door/window placement."),
                    "Use revit.catalog kind=familySymbols preset=placement to discover familySymbolId and placementType.",
                    new Dictionary<string, object>
                    {
                        ["wallHostedDoorWindowSymbols"] = familyPlacementWallHostedSymbols.Length,
                        ["levelBasedFurnitureEquipmentFixtureSymbols"] = familyPlacementLevelBasedSymbols.Length,
                        ["wallHostedReady"] = levels.Length > 0 && familyPlacementWallCount > 0 && familyPlacementWallHostedSymbols.Length > 0,
                        ["levelBasedReady"] = levels.Length > 0 && familyPlacementLevelBasedSymbols.Length > 0,
                        ["sampleHostedFamilySymbolId"] = familyPlacementWallHostedSymbols.FirstOrDefault() == null ? null : ToElementIdString(familyPlacementWallHostedSymbols.First().Id),
                        ["sampleLevelBasedFamilySymbolId"] = familyPlacementLevelBasedSymbols.FirstOrDefault() == null ? null : ToElementIdString(familyPlacementLevelBasedSymbols.First().Id)
                    }));
            }
            if (WantsScenario("selection"))
            {
                UIDocument uidocument = app.ActiveUIDocument;
                bool selectionAvailable = uidocument != null && ReferenceEquals(uidocument.Document, document);
                int selectionCount = selectionAvailable ? uidocument.Selection.GetElementIds().Count : 0;
                scenarios.Add(ScenarioReadiness(
                    "selection",
                    selectionAvailable && selectionCount > 0,
                    selectionAvailable
                        ? selectionCount > 0 ? Array.Empty<string>() : new[] { "At least one selected element." }
                        : new[] { "The requested document must be the active UI document." },
                    "Use revit.query filters when no active selection is available.",
                    new Dictionary<string, object> { ["available"] = selectionAvailable, ["selectionCount"] = selectionCount }));
            }
            if (WantsScenario("annotations"))
            {
                bool activeGraphicalView = IsGraphicalView(activeView);
                int annotationTextNoteTypeCount = GetTextNoteTypeCount();
                scenarios.Add(ScenarioReadiness(
                    "annotations",
                    activeGraphicalView,
                    activeGraphicalView ? Array.Empty<string>() : new[] { "An active graphical non-template view." },
                    "Annotation operations should be scoped to an explicit graphical view and valid references.",
                    new Dictionary<string, object> { ["activeGraphicalView"] = activeGraphicalView, ["textNoteTypeCount"] = annotationTextNoteTypeCount }));
            }

            if (!GetBool(payload, "includeHints", true))
            {
                foreach (Dictionary<string, object> scenario in scenarios)
                {
                    scenario.Remove("hints");
                }
            }

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["levels"] = new Dictionary<string, object>
                {
                    ["count"] = levels.Length,
                    ["sample"] = levels.Take(8).Select(BuildLevelSummary).ToArray()
                },
                ["activeView"] = activeView == null ? null : BuildViewSummary(activeView),
                ["scenarios"] = scenarios,
                ["readyCount"] = scenarios.Count(scenario => GetBool(scenario, "ready", false)),
                ["totalCount"] = scenarios.Count,
                ["source"] = "revit-addin"
            };

            return data;
        }

        private static Dictionary<string, object> ScenarioReadiness(
            string name,
            bool ready,
            IEnumerable<string> missingPrerequisites,
            string nextAction,
            Dictionary<string, object> hints)
        {
            string[] missing = (missingPrerequisites ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            var scenario = new Dictionary<string, object>
            {
                ["name"] = name,
                ["ready"] = ready,
                ["missing"] = missing,
                ["missingPrerequisites"] = missing
            };
            if (!string.IsNullOrWhiteSpace(nextAction)) scenario["nextAction"] = nextAction;
            if (hints != null && hints.Count > 0) scenario["hints"] = hints;
            return scenario;
        }

        private static IEnumerable<string> MissingPrerequisites(params string[] values)
        {
            return values == null ? Enumerable.Empty<string>() : values.Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private static int CountTypeChangeCandidates(Document document, int maxScan, out int scanned, out bool truncated)
        {
            int candidates = 0;
            scanned = 0;
            truncated = false;

            foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
            {
                if (scanned >= maxScan)
                {
                    truncated = true;
                    break;
                }

                scanned++;
                if (element == null || element.Pinned) continue;
                ElementId currentTypeId = element.GetTypeId();
                if (!IsValidElementId(currentTypeId)) continue;

                try
                {
                    ICollection<ElementId> validTypeIds = element.GetValidTypes();
                    if (validTypeIds != null && validTypeIds.Count(id => IsValidElementId(id) && !string.Equals(ToElementIdString(id), ToElementIdString(currentTypeId), StringComparison.Ordinal)) > 0)
                    {
                        candidates++;
                    }
                }
                catch
                {
                    // Some element classes do not expose valid type sets; ignore them for readiness.
                }
            }

            return candidates;
        }

        private static bool IsGraphicalView(View view)
        {
            if (view == null || view.IsTemplate) return false;

            return SafeCanBePrinted(view);
        }

        private static bool SafeCanBePrinted(View view)
        {
            if (view == null) return false;

            try
            {
                return view.CanBePrinted;
            }
            catch
            {
                return false;
            }
        }

        private Dictionary<string, object> BuildStatus(UIApplication app)
        {
            UIDocument activeUiDocument = app.ActiveUIDocument;
            Document activeDocument = activeUiDocument?.Document;
            var data = new Dictionary<string, object>
            {
                ["connected"] = true,
                ["brokerVersion"] = "unknown",
                ["addinVersion"] = AddinVersion,
                ["addinAssembly"] = BuildAddinAssemblyIdentity(),
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
                ["capabilities"] = new[]
                {
                    "revit.status",
                    "revit.list_documents",
                    "revit.create_project_from_template",
                    "revit.get_levels",
                    "revit.get_views",
                    "revit.get_sheets",
                    "revit.get_current_view",
                    "revit.get_current_view_elements",
                    "revit.get_selection",
                    "revit.analyze_model",
                    "revit.get_model_readiness",
                    "revit.get_model_context",
                    "revit.get_material_quantities",
                    "revit.get_warnings",
                    "revit.get_rooms",
                    "revit.catalog",
                    "revit.query",
                    "revit.describe_parameters",
                    "revit.preview_change_set",
                    "revit.apply_change_set",
                    "revit.cancel_request"
                },
                ["warnings"] = Array.Empty<object>()
            };

            if (activeDocument != null)
            {
                data["activeDocument"] = BuildDocumentSummary(activeDocument, activeDocument);
            }

            return data;
        }

        private object[] BuildDocumentList(UIApplication app)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            return EnumerateDocuments(app)
                .Select(document => BuildDocumentSummary(document, activeDocument))
                .ToArray();
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
            List<BridgeWarning> warnings,
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

            IEnumerable<Element> elements = collector.ToElements();
            if (targetElement != null)
            {
                if (validTypeIds == null || validTypeIds.Count == 0)
                {
                    warnings.Add(new BridgeWarning
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

        private static bool MatchesViewFilter(View view, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> viewIds = GetStringList(filter, "viewIds");
            if (viewIds.Count > 0 && !viewIds.Contains(ToElementIdString(view.Id), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (uniqueIds.Count > 0 && !uniqueIds.Contains(view.UniqueId, StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> viewTypes = GetStringList(filter, "viewTypes");
            if (viewTypes.Count > 0 && !viewTypes.Contains(view.ViewType.ToString(), StringComparer.OrdinalIgnoreCase)) return false;

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (SafeElementName(view) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            bool? isTemplate = GetNullableBool(filter, "isTemplate");
            if (isTemplate.HasValue && view.IsTemplate != isTemplate.Value) return false;

            bool? isGraphical = GetNullableBool(filter, "isGraphical");
            if (isGraphical.HasValue && IsGraphicalView(view) != isGraphical.Value) return false;

            bool? canBePrinted = GetNullableBool(filter, "canBePrinted");
            if (canBePrinted.HasValue && SafeCanBePrinted(view) != canBePrinted.Value) return false;

            return true;
        }

        private static bool MatchesSheetFilter(Document document, ViewSheet sheet, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> sheetIds = GetStringList(filter, "sheetIds");
            if (sheetIds.Count > 0 && !sheetIds.Contains(ToElementIdString(sheet.Id), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (uniqueIds.Count > 0 && !uniqueIds.Contains(sheet.UniqueId, StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> numbers = GetStringList(filter, "numbers");
            if (numbers.Count > 0 && !numbers.Contains(sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)) return false;

            string numberContains = GetString(filter, "numberContains");
            if (!string.IsNullOrWhiteSpace(numberContains) &&
                (sheet.SheetNumber ?? string.Empty).IndexOf(numberContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (SafeElementName(sheet) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            IReadOnlyList<string> titleBlockIds = GetStringList(filter, "titleBlockIds");
            if (titleBlockIds.Count > 0)
            {
                string[] sheetTitleBlockIds = GetSheetTitleBlockIds(document, sheet).ToArray();
                if (!titleBlockIds.Any(id => sheetTitleBlockIds.Contains(id, StringComparer.OrdinalIgnoreCase))) return false;
            }

            return true;
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
            try
            {
                return collector.ToElementIds().Count;
            }
            catch
            {
                return collector.ToElements().Count;
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

        private static object[] BuildCategoryBuckets(IEnumerable<Element> elements, int limit)
        {
            return elements
                .GroupBy(GetCategoryBucketKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(group =>
                {
                    Element sample = group.FirstOrDefault();
                    string builtInCategory = sample == null ? string.Empty : GetBuiltInCategoryName(sample);
                    var bucket = new Dictionary<string, object>
                    {
                        ["key"] = group.Key,
                        ["name"] = sample?.Category?.Name ?? group.Key,
                        ["count"] = group.Count()
                    };
                    if (!string.IsNullOrWhiteSpace(builtInCategory)) bucket["builtInCategory"] = builtInCategory;
                    return bucket;
                })
                .ToArray();
        }

        private static object[] BuildClassBuckets(IEnumerable<Element> elements, int limit)
        {
            return elements
                .GroupBy(element => element.GetType().Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(group => new Dictionary<string, object>
                {
                    ["key"] = group.Key,
                    ["count"] = group.Count()
                })
                .ToArray();
        }

        private static object[] BuildLevelBuckets(Document document, IEnumerable<Element> elements, int limit)
        {
            Dictionary<string, string> levelNames = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToDictionary(level => ToElementIdString(level.Id), level => level.Name, StringComparer.OrdinalIgnoreCase);

            return elements
                .Select(element => ToElementIdString(GetLevelId(element)))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(group => new Dictionary<string, object>
                {
                    ["key"] = group.Key,
                    ["name"] = levelNames.TryGetValue(group.Key, out string name) ? name : group.Key,
                    ["count"] = group.Count()
                })
                .ToArray();
        }

        private static string GetCategoryBucketKey(Element element)
        {
            string builtInCategory = GetBuiltInCategoryName(element);
            if (!string.IsNullOrWhiteSpace(builtInCategory)) return builtInCategory;
            return element.Category?.Name ?? "(none)";
        }

        private static bool AccumulateMaterialQuantities(
            Document document,
            Element element,
            bool includePaint,
            Dictionary<string, MaterialQuantityAccumulator> accumulators,
            List<BridgeWarning> warnings)
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
            List<BridgeWarning> warnings)
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

        private static void AddWarningOnce(List<BridgeWarning> warnings, string code, string message)
        {
            if (warnings.Any(warning => string.Equals(warning.Code, code, StringComparison.OrdinalIgnoreCase))) return;
            warnings.Add(new BridgeWarning
            {
                Code = code,
                Message = message
            });
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

        private static bool MatchesWarningFilter(FailureMessage failure, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> severities = GetStringList(filter, "severities");
            if (severities.Count > 0 && !severities.Contains(WarningSeverity(failure), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> failureDefinitionIds = GetStringList(filter, "failureDefinitionIds");
            if (failureDefinitionIds.Count > 0 && !failureDefinitionIds.Contains(WarningDefinitionId(failure), StringComparer.OrdinalIgnoreCase)) return false;

            string descriptionContains = GetString(filter, "descriptionContains");
            if (!string.IsNullOrWhiteSpace(descriptionContains) &&
                (WarningDescription(failure) ?? string.Empty).IndexOf(descriptionContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            IReadOnlyList<string> elementIds = GetStringList(filter, "elementIds");
            if (elementIds.Count > 0)
            {
                var ids = new HashSet<string>(WarningElementIds(failure, includeAdditional: true), StringComparer.OrdinalIgnoreCase);
                if (!elementIds.Any(id => ids.Contains(id))) return false;
            }

            return true;
        }

        private static Dictionary<string, object> BuildWarningItem(FailureMessage failure, IReadOnlyList<string> fields, int ordinal)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = BuildWarningId(failure, ordinal)
            };

            List<string> failingElementIds = null;
            List<string> additionalElementIds = null;

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "severity":
                        item["severity"] = WarningSeverity(failure);
                        break;
                    case "description":
                        item["description"] = WarningDescription(failure);
                        break;
                    case "failureDefinitionId":
                        string definitionId = WarningDefinitionId(failure);
                        if (!string.IsNullOrWhiteSpace(definitionId)) item["failureDefinitionId"] = definitionId;
                        break;
                    case "defaultResolution":
                        string resolution = WarningDefaultResolution(failure);
                        if (!string.IsNullOrWhiteSpace(resolution)) item["defaultResolution"] = resolution;
                        break;
                    case "failingElementIds":
                        failingElementIds = failingElementIds ?? WarningElementIds(failure, includeAdditional: false).ToList();
                        item["failingElementIds"] = failingElementIds.Take(MaxWarningElementIds).ToArray();
                        if (failingElementIds.Count > MaxWarningElementIds) item["failingElementIdsTruncated"] = true;
                        break;
                    case "additionalElementIds":
                        additionalElementIds = additionalElementIds ?? WarningAdditionalElementIds(failure).ToList();
                        item["additionalElementIds"] = additionalElementIds.Take(MaxWarningElementIds).ToArray();
                        if (additionalElementIds.Count > MaxWarningElementIds) item["additionalElementIdsTruncated"] = true;
                        break;
                    case "failingElementCount":
                        failingElementIds = failingElementIds ?? WarningElementIds(failure, includeAdditional: false).ToList();
                        item["failingElementCount"] = failingElementIds.Count;
                        break;
                    case "additionalElementCount":
                        additionalElementIds = additionalElementIds ?? WarningAdditionalElementIds(failure).ToList();
                        item["additionalElementCount"] = additionalElementIds.Count;
                        break;
                    case "failingElementIdsTruncated":
                        failingElementIds = failingElementIds ?? WarningElementIds(failure, includeAdditional: false).ToList();
                        item["failingElementIdsTruncated"] = failingElementIds.Count > MaxWarningElementIds;
                        break;
                    case "additionalElementIdsTruncated":
                        additionalElementIds = additionalElementIds ?? WarningAdditionalElementIds(failure).ToList();
                        item["additionalElementIdsTruncated"] = additionalElementIds.Count > MaxWarningElementIds;
                        break;
                }
            }

            return item;
        }

        private static string BuildWarningId(FailureMessage failure, int ordinal)
        {
            string definitionId = WarningDefinitionId(failure);
            string firstElementId = FirstWarningElementId(failure);
            string seed = (definitionId + "|" + WarningDescription(failure) + "|" + firstElementId + "|" + ordinal.ToString(CultureInfo.InvariantCulture))
                .Trim('|');
            if (string.IsNullOrWhiteSpace(seed)) seed = "warning|" + ordinal.ToString(CultureInfo.InvariantCulture);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                return "wrn_" + BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string WarningSeverity(FailureMessage failure)
        {
            try
            {
                return failure?.GetSeverity().ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WarningDescription(FailureMessage failure)
        {
            try
            {
                return failure?.GetDescriptionText() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WarningDefinitionId(FailureMessage failure)
        {
            try
            {
                return failure?.GetFailureDefinitionId()?.Guid.ToString("D") ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WarningDefaultResolution(FailureMessage failure)
        {
            try
            {
                return failure?.GetDefaultResolutionCaption() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FirstWarningElementId(FailureMessage failure)
        {
            return WarningElementIds(failure, includeAdditional: true).FirstOrDefault() ?? string.Empty;
        }

        private static IEnumerable<string> WarningElementIds(FailureMessage failure, bool includeAdditional)
        {
            IEnumerable<string> failing = SafeWarningElementIds(() => failure?.GetFailingElements());
            if (!includeAdditional) return failing;
            return failing.Concat(WarningAdditionalElementIds(failure)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> WarningAdditionalElementIds(FailureMessage failure)
        {
            return SafeWarningElementIds(() => failure?.GetAdditionalElements());
        }

        private static IEnumerable<string> SafeWarningElementIds(Func<ICollection<ElementId>> read)
        {
            try
            {
                ICollection<ElementId> ids = read();
                if (ids == null) return Enumerable.Empty<string>();
                return ids.Where(IsValidElementId).Select(ToElementIdString).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static IEnumerable<Room> CreateRoomElements(Document document, Dictionary<string, object> filter, List<BridgeWarning> warnings)
        {
            IReadOnlyList<string> elementIds = GetStringList(filter, "elementIds");
            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (elementIds.Count > 0 || uniqueIds.Count > 0)
            {
                var rooms = new List<Room>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                            Code = "INVALID_ROOM_ID",
                            Message = "filter.elementIds contains an invalid room ElementId; it was ignored."
                        });
                    }

                    Room room = element as Room;
                    if (room == null) continue;
                    string key = ToElementIdString(room.Id);
                    if (seen.Add(key)) rooms.Add(room);
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
                            Code = "INVALID_ROOM_UNIQUE_ID",
                            Message = "filter.uniqueIds contains an invalid room UniqueId; it was ignored."
                        });
                    }

                    Room room = element as Room;
                    if (room == null) continue;
                    string key = ToElementIdString(room.Id);
                    if (seen.Add(key)) rooms.Add(room);
                }

                return rooms;
            }

            return new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>();
        }

        private static bool MatchesRoomFilter(Document document, Room room, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> levelIds = GetStringList(filter, "levelIds");
            if (levelIds.Count > 0 && !levelIds.Contains(ToElementIdString(GetLevelId(room)), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> phaseIds = GetStringList(filter, "phaseIds");
            if (phaseIds.Count > 0 && !phaseIds.Contains(ToElementIdString(GetCreatedPhaseId(room)), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> numbers = GetStringList(filter, "numbers");
            string number = GetRoomNumber(room);
            if (numbers.Count > 0 && !numbers.Contains(number, StringComparer.OrdinalIgnoreCase)) return false;

            string numberContains = GetString(filter, "numberContains");
            if (!string.IsNullOrWhiteSpace(numberContains) &&
                (number ?? string.Empty).IndexOf(numberContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (GetRoomName(room) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string departmentContains = GetString(filter, "departmentContains");
            if (!string.IsNullOrWhiteSpace(departmentContains) &&
                (GetRoomDepartment(room) ?? string.Empty).IndexOf(departmentContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            return true;
        }

        private static Dictionary<string, object> BuildRoomItem(Document document, Room room, IReadOnlyList<string> fields)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(room.Id)
            };

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "uniqueId":
                        item["uniqueId"] = room.UniqueId;
                        break;
                    case "number":
                        item["number"] = GetRoomNumber(room);
                        break;
                    case "name":
                        item["name"] = GetRoomName(room);
                        break;
                    case "levelId":
                        ElementId levelId = GetLevelId(room);
                        if (IsValidElementId(levelId)) item["levelId"] = ToElementIdString(levelId);
                        break;
                    case "levelName":
                        string levelName = GetRoomLevelName(document, room);
                        if (!string.IsNullOrWhiteSpace(levelName)) item["levelName"] = levelName;
                        break;
                    case "phaseId":
                        ElementId phaseId = GetCreatedPhaseId(room);
                        if (IsValidElementId(phaseId)) item["phaseId"] = ToElementIdString(phaseId);
                        break;
                    case "phaseName":
                        ElementId phaseNameId = GetCreatedPhaseId(room);
                        Element phase = IsValidElementId(phaseNameId) ? document.GetElement(phaseNameId) : null;
                        if (phase != null) item["phaseName"] = SafeElementName(phase);
                        break;
                    case "area":
                        double area = SafeRoomArea(room);
                        if (area > 0) item["area"] = AreaValue(area);
                        break;
                    case "volume":
                        double volume = SafeRoomVolume(room);
                        if (volume > 0) item["volume"] = VolumeValue(volume);
                        break;
                    case "perimeter":
                        Parameter perimeter = room.get_Parameter(BuiltInParameter.ROOM_PERIMETER);
                        if (perimeter != null && perimeter.StorageType == StorageType.Double && perimeter.AsDouble() > 0)
                        {
                            item["perimeter"] = LengthValue(perimeter.AsDouble());
                        }
                        break;
                    case "location":
                        LocationPoint point = room.Location as LocationPoint;
                        if (point != null) item["location"] = PointValue(point.Point);
                        break;
                    case "isPlaced":
                        item["isPlaced"] = IsRoomPlaced(room);
                        break;
                    case "isEnclosed":
                        item["isEnclosed"] = SafeRoomArea(room) > 0;
                        break;
                    case "department":
                        string department = GetRoomDepartment(room);
                        if (!string.IsNullOrWhiteSpace(department)) item["department"] = department;
                        break;
                    default:
                        if (field.StartsWith("param:", StringComparison.OrdinalIgnoreCase))
                        {
                            string parameterName = field.Substring("param:".Length);
                            Parameter parameter = room.LookupParameter(parameterName);
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

        private static Dictionary<string, object> BuildParameterTarget(
            Document document,
            Element element,
            bool includeTypeParameters,
            bool includeReadOnly,
            bool includeValues,
            string nameContains,
            int parameterLimit)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };

            if (element.Category != null) item["category"] = element.Category.Name;

            ElementId typeId = element.GetTypeId();
            Element typeElement = null;
            if (IsValidElementId(typeId))
            {
                item["typeId"] = ToElementIdString(typeId);
                typeElement = document.GetElement(typeId);
                if (typeElement != null) item["typeName"] = SafeElementName(typeElement);
            }

            var parameters = new List<Dictionary<string, object>>();
            AddParameterSummaries(parameters, element, "instance", includeReadOnly, includeValues, nameContains);

            if (includeTypeParameters && typeElement != null && !string.Equals(ToElementIdString(typeElement.Id), ToElementIdString(element.Id), StringComparison.Ordinal))
            {
                AddParameterSummaries(parameters, typeElement, "type", includeReadOnly, includeValues, nameContains);
            }

            List<Dictionary<string, object>> ordered = parameters
                .OrderBy(parameter => Convert.ToString(parameter["source"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                .ThenBy(parameter => Convert.ToString(parameter["name"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                .ToList();

            item["parameters"] = ordered.Take(parameterLimit).ToArray();
            item["parameterCount"] = ordered.Count;
            item["truncated"] = ordered.Count > parameterLimit;
            return item;
        }

        private static string NormalizeParameterDescribePreset(string preset)
        {
            if (string.Equals(preset, "full", StringComparison.OrdinalIgnoreCase)) return "full";
            if (string.Equals(preset, "namesOnly", StringComparison.OrdinalIgnoreCase)) return "namesOnly";
            return "writableEdit";
        }

        private static int DefaultParameterElementLimit(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal) ? 20 : 10;
        }

        private static int DefaultParameterLimit(string preset)
        {
            if (string.Equals(preset, "full", StringComparison.Ordinal)) return 80;
            if (string.Equals(preset, "namesOnly", StringComparison.Ordinal)) return 120;
            return 40;
        }

        private static bool DefaultIncludeTypeParameters(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal) ||
                string.Equals(preset, "namesOnly", StringComparison.Ordinal);
        }

        private static bool DefaultIncludeReadOnlyParameters(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal) ||
                string.Equals(preset, "namesOnly", StringComparison.Ordinal);
        }

        private static bool DefaultIncludeParameterValues(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal);
        }

        private static void AddParameterSummaries(
            List<Dictionary<string, object>> target,
            Element element,
            string source,
            bool includeReadOnly,
            bool includeValues,
            string nameContains)
        {
            if (element == null || element.Parameters == null) return;

            foreach (Parameter parameter in element.Parameters.Cast<Parameter>())
            {
                Dictionary<string, object> summary = BuildParameterSummary(parameter, source, includeValues);
                string name = Convert.ToString(summary["name"], CultureInfo.InvariantCulture);
                if (!includeReadOnly && GetBool(summary, "isReadOnly", false)) continue;
                if (!string.IsNullOrWhiteSpace(nameContains) &&
                    (name ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                target.Add(summary);
            }
        }

        private static Dictionary<string, object> BuildParameterSummary(Parameter parameter, string source, bool includeValues)
        {
            string name = parameter?.Definition?.Name ?? "(unnamed)";
            var summary = new Dictionary<string, object>
            {
                ["name"] = name,
                ["storageType"] = parameter == null ? "None" : parameter.StorageType.ToString(),
                ["source"] = source,
                ["isReadOnly"] = parameter?.IsReadOnly ?? true
            };

            if (parameter == null) return summary;

            try
            {
                object hasValue = typeof(Parameter).GetProperty("HasValue")?.GetValue(parameter, null);
                if (hasValue is bool hasValueBool) summary["hasValue"] = hasValueBool;
            }
            catch
            {
                // Older parameter flavors may not expose HasValue reliably.
            }

            try
            {
                object idValue = typeof(Parameter).GetProperty("Id")?.GetValue(parameter, null);
                if (idValue is ElementId parameterId && IsValidElementId(parameterId))
                {
                    summary["definitionId"] = GetElementIdValue(parameterId).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Parameter ids are unavailable for some built-in/internal parameter flavors.
            }

            try
            {
                object isShared = typeof(Parameter).GetProperty("IsShared")?.GetValue(parameter, null);
                if (isShared is bool isSharedBool) summary["isShared"] = isSharedBool;
            }
            catch
            {
                // Not all parameter sources expose shared state.
            }

            ExternalDefinition externalDefinition = parameter.Definition as ExternalDefinition;
            if (externalDefinition != null)
            {
                summary["guid"] = externalDefinition.GUID.ToString("D");
            }

            if (includeValues)
            {
                object value = ParameterValue(parameter);
                summary["value"] = value;

                if (parameter.StorageType == StorageType.ElementId)
                {
                    ElementId elementId = parameter.AsElementId();
                    if (IsValidElementId(elementId)) summary["elementIdValue"] = ToElementIdString(elementId);
                }

                try
                {
                    string valueString = parameter.AsValueString();
                    if (!string.IsNullOrWhiteSpace(valueString)) summary["valueString"] = valueString;
                }
                catch
                {
                    // Some storage types do not provide display strings.
                }
            }

            return summary;
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
                    defaults = GeometrySummaryFields();
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

        private static string[] NormalizeRoomFields(IReadOnlyList<string> requested, string preset, List<BridgeWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "schedule":
                    defaults = new[] { "id", "number", "name", "levelId", "levelName", "area", "volume", "department" };
                    break;
                default:
                    defaults = new[] { "id", "uniqueId", "number", "name", "levelId", "area" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedRoomField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedRoomField(field))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "UNSUPPORTED_ROOM_FIELD",
                        Message = "Room field '" + field + "' is not supported by the current room projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedRoomField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "number":
                case "name":
                case "levelId":
                case "levelName":
                case "phaseId":
                case "phaseName":
                case "area":
                case "volume":
                case "perimeter":
                case "location":
                case "isPlaced":
                case "isEnclosed":
                case "department":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }

        private static string[] SummaryFields()
        {
            return new[] { "id", "uniqueId", "category", "class", "name", "typeId", "levelId" };
        }

        private static string[] GeometrySummaryFields()
        {
            return new[] { "id", "uniqueId", "category", "class", "name", "typeId", "levelId", "location", "bounds" };
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
                case "location":
                case "bounds":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }

        private static string[] NormalizeWarningFields(IReadOnlyList<string> requested, string preset, List<BridgeWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "elements":
                    defaults = new[] { "id", "severity", "description", "failingElementIds", "additionalElementIds", "failingElementCount", "additionalElementCount" };
                    break;
                case "full":
                    defaults = new[]
                    {
                        "id",
                        "severity",
                        "description",
                        "failureDefinitionId",
                        "defaultResolution",
                        "failingElementIds",
                        "additionalElementIds",
                        "failingElementCount",
                        "additionalElementCount"
                    };
                    break;
                default:
                    defaults = new[] { "id", "severity", "description", "failingElementCount", "additionalElementCount" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedWarningField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedWarningField(field))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "UNSUPPORTED_WARNING_FIELD",
                        Message = "Warning field '" + field + "' is not supported by the current warning projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedWarningField(string field)
        {
            switch (field)
            {
                case "id":
                case "severity":
                case "description":
                case "failureDefinitionId":
                case "defaultResolution":
                case "failingElementIds":
                case "additionalElementIds":
                case "failingElementCount":
                case "additionalElementCount":
                case "failingElementIdsTruncated":
                case "additionalElementIdsTruncated":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] NormalizeViewFields(IReadOnlyList<string> requested, string preset, bool includeCropBox, List<BridgeWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "sheetPlacement":
                    defaults = new[] { "id", "uniqueId", "name", "type", "isGraphical", "isTemplate", "canBePrinted", "viewTemplateId" };
                    break;
                default:
                    defaults = new[] { "id", "uniqueId", "name", "type", "isGraphical", "isTemplate", "canBePrinted", "scale", "detailLevel", "discipline" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (string.Equals(field, "cropBox", StringComparison.OrdinalIgnoreCase) && !includeCropBox)
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "CROP_BOX_NOT_INCLUDED",
                        Message = "Field cropBox requires includeCropBox=true."
                    });
                    continue;
                }

                if (IsSupportedViewField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedViewField(field))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "UNSUPPORTED_VIEW_FIELD",
                        Message = "View field '" + field + "' is not supported by the current view projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedViewField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "name":
                case "type":
                case "isGraphical":
                case "isTemplate":
                case "canBePrinted":
                case "scale":
                case "detailLevel":
                case "discipline":
                case "viewTemplateId":
                case "viewTemplateName":
                case "associatedLevelId":
                case "associatedLevelName":
                case "cropBoxActive":
                case "cropBoxVisible":
                case "cropBox":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] NormalizeSheetFields(IReadOnlyList<string> requested, string preset, bool includePlacedViews, List<BridgeWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "placement":
                    defaults = new[] { "id", "uniqueId", "sheetNumber", "name", "titleBlockIds", "placedViews" };
                    break;
                default:
                    defaults = new[] { "id", "uniqueId", "sheetNumber", "name", "titleBlockIds" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (string.Equals(field, "placedViews", StringComparison.OrdinalIgnoreCase) && !includePlacedViews)
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "PLACED_VIEWS_NOT_INCLUDED",
                        Message = "Field placedViews requires includePlacedViews=true."
                    });
                    continue;
                }

                if (IsSupportedSheetField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedSheetField(field))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "UNSUPPORTED_SHEET_FIELD",
                        Message = "Sheet field '" + field + "' is not supported by the current sheet projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedSheetField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "sheetNumber":
                case "name":
                case "titleBlockIds":
                case "placedViews":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] NormalizeCatalogFields(IReadOnlyList<string> requested, string preset, List<BridgeWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "typeChange":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "isCurrentType", "validForTarget" };
                    break;
                case "placement":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "familyId", "isActive", "placementType" };
                    break;
                case "sheet":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "familyId", "isActive" };
                    break;
                case "annotation":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "familyId" };
                    break;
                default:
                    defaults = new[] { "id", "class", "category", "name", "familyName" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedCatalogField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedCatalogField(field))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "UNSUPPORTED_CATALOG_FIELD",
                        Message = "Catalog field '" + field + "' is not supported by the current catalog projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedCatalogField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "class":
                case "category":
                case "builtInCategory":
                case "name":
                case "familyName":
                case "familyId":
                case "isCurrentType":
                case "validForTarget":
                case "isActive":
                case "placementType":
                case "viewFamily":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }

        private Dictionary<string, object> BuildDocumentSummary(Document document, Document activeDocument)
        {
            var summary = new Dictionary<string, object>
            {
                ["documentId"] = GetDocumentId(document),
                ["title"] = document.Title,
                ["fingerprint"] = ComputeDocumentFingerprint(document),
                ["isActive"] = ReferenceEquals(document, activeDocument),
                ["isWorkshared"] = document.IsWorkshared,
                ["isModified"] = document.IsModified,
                ["generation"] = _generations.GetGeneration(document)
            };

            if (!string.IsNullOrWhiteSpace(document.PathName)) summary["path"] = document.PathName;

            View activeView = SafeActiveView(document);
            if (activeView != null)
            {
                summary["activeView"] = BuildViewSummary(activeView);
            }

            return summary;
        }

        private static Dictionary<string, object> BuildDocumentReference(Document document, long generation)
        {
            var summary = new Dictionary<string, object>
            {
                ["fingerprint"] = ComputeDocumentFingerprint(document),
                ["title"] = document.Title,
                ["generation"] = generation
            };

            if (!string.IsNullOrWhiteSpace(document.PathName)) summary["path"] = document.PathName;
            return summary;
        }

        private static Dictionary<string, object> BuildViewItem(Document document, View view, IReadOnlyList<string> fields, bool includeCropBox)
        {
            Dictionary<string, object> full = BuildViewInfo(document, view, includeCropBox);
            var item = new Dictionary<string, object> { ["id"] = ToElementIdString(view.Id) };

            foreach (string field in fields)
            {
                if (string.Equals(field, "id", StringComparison.OrdinalIgnoreCase)) continue;
                if (full.TryGetValue(field, out object value))
                {
                    item[field] = value;
                }
            }

            return item;
        }

        private static Dictionary<string, object> BuildSheetItem(Document document, ViewSheet sheet, IReadOnlyList<string> fields, bool includePlacedViews)
        {
            var item = new Dictionary<string, object> { ["id"] = ToElementIdString(sheet.Id) };

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "uniqueId":
                        item["uniqueId"] = sheet.UniqueId;
                        break;
                    case "sheetNumber":
                        item["sheetNumber"] = sheet.SheetNumber;
                        break;
                    case "name":
                        item["name"] = SafeElementName(sheet);
                        break;
                    case "titleBlockIds":
                        item["titleBlockIds"] = GetSheetTitleBlockIds(document, sheet).ToArray();
                        break;
                    case "placedViews":
                        if (includePlacedViews) item["placedViews"] = GetSheetPlacedViews(document, sheet).ToArray();
                        break;
                }
            }

            return item;
        }

        private static IEnumerable<string> GetSheetTitleBlockIds(Document document, ViewSheet sheet)
        {
            try
            {
                return new FilteredElementCollector(document, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Where(IsValidElementId)
                    .Select(ToElementIdString)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<Dictionary<string, object>> GetSheetPlacedViews(Document document, ViewSheet sheet)
        {
            ICollection<ElementId> viewportIds;
            try
            {
                viewportIds = sheet.GetAllViewports();
            }
            catch
            {
                return Array.Empty<Dictionary<string, object>>();
            }

            var placedViews = new List<Dictionary<string, object>>();
            foreach (ElementId viewportId in viewportIds)
            {
                Viewport viewport = document.GetElement(viewportId) as Viewport;
                if (viewport == null) continue;
                View view = document.GetElement(viewport.ViewId) as View;
                var item = new Dictionary<string, object>
                {
                    ["viewportId"] = ToElementIdString(viewport.Id),
                    ["viewId"] = ToElementIdString(viewport.ViewId)
                };

                if (view != null)
                {
                    item["viewName"] = SafeElementName(view);
                    item["viewType"] = view.ViewType.ToString();
                }

                try
                {
                    item["center"] = PointValue(viewport.GetBoxCenter());
                }
                catch
                {
                    // Viewport center can be unavailable for unusual sheet contents.
                }

                placedViews.Add(item);
            }

            return placedViews;
        }

        private static Dictionary<string, object> BuildViewSummary(View view)
        {
            bool canBePrinted = SafeCanBePrinted(view);

            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(view.Id),
                ["uniqueId"] = view.UniqueId,
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
                ["isGraphical"] = !view.IsTemplate && canBePrinted,
                ["isTemplate"] = view.IsTemplate,
                ["canBePrinted"] = canBePrinted
            };

            try
            {
                if (view.Scale > 0) summary["scale"] = view.Scale;
            }
            catch
            {
                // Some Revit view-like elements do not expose scale.
            }

            try
            {
                summary["detailLevel"] = view.DetailLevel.ToString();
            }
            catch
            {
                // Detail level is not available on every view type.
            }

            try
            {
                summary["discipline"] = view.Discipline.ToString();
            }
            catch
            {
                // Discipline is not available on every view type.
            }

            return summary;
        }

        private static Dictionary<string, object> BuildViewInfo(Document document, View view, bool includeCropBox)
        {
            Dictionary<string, object> info = BuildViewSummary(view);

            try
            {
                ElementId viewTemplateId = view.ViewTemplateId;
                if (IsValidElementId(viewTemplateId))
                {
                    info["viewTemplateId"] = ToElementIdString(viewTemplateId);
                    Element viewTemplate = document.GetElement(viewTemplateId);
                    if (viewTemplate != null) info["viewTemplateName"] = SafeElementName(viewTemplate);
                }
            }
            catch
            {
                // View templates are unavailable for some views.
            }

            Level associatedLevel = GetAssociatedLevel(view);
            if (associatedLevel != null)
            {
                info["associatedLevelId"] = ToElementIdString(associatedLevel.Id);
                info["associatedLevelName"] = associatedLevel.Name;
            }

            try
            {
                info["cropBoxActive"] = view.CropBoxActive;
            }
            catch
            {
                // Crop settings are not exposed on every view type.
            }

            try
            {
                info["cropBoxVisible"] = view.CropBoxVisible;
            }
            catch
            {
                // Crop settings are not exposed on every view type.
            }

            if (includeCropBox)
            {
                try
                {
                    BoundingBoxXYZ cropBox = view.CropBox;
                    if (cropBox != null)
                    {
                        info["cropBox"] = new Dictionary<string, object>
                        {
                            ["min"] = PointValue(cropBox.Min),
                            ["max"] = PointValue(cropBox.Max)
                        };
                    }
                }
                catch
                {
                    // Crop boxes are unavailable for non-graphical views.
                }
            }

            return info;
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

        private static Level GetAssociatedLevel(View view)
        {
            if (view == null) return null;

            try
            {
                object value = typeof(View).GetProperty("GenLevel")?.GetValue(view, null);
                Level level = value as Level;
                if (level != null) return level;
            }
            catch
            {
                // Fall through to parameter-based lookup.
            }

            try
            {
                if (Enum.IsDefined(typeof(BuiltInParameter), "PLAN_VIEW_LEVEL"))
                {
                    var builtInParameter = (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), "PLAN_VIEW_LEVEL");
                    Parameter parameter = view.get_Parameter(builtInParameter);
                    if (parameter != null && parameter.StorageType == StorageType.ElementId)
                    {
                        return view.Document.GetElement(parameter.AsElementId()) as Level;
                    }
                }
            }
            catch
            {
                // Some view types do not have an associated level.
            }

            return null;
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
            return DocumentGenerationTracker.ComputeDocumentFingerprint(document);
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

        private static HashSet<string> GetValidTypeIdSet(Element element, List<BridgeWarning> warnings)
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
                warnings.Add(new BridgeWarning
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

        private static string GetPlacementType(Element element)
        {
            FamilySymbol symbol = element as FamilySymbol;
            return symbol?.Family?.FamilyPlacementType.ToString() ?? string.Empty;
        }

        private static bool IsSupportedWallHostedFamilySymbol(FamilySymbol symbol)
        {
            return symbol != null &&
                   symbol.Family != null &&
                   symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted &&
                   IsWallHostedDoorWindowCategory(symbol);
        }

        private static bool IsSupportedLevelBasedFamilySymbol(FamilySymbol symbol)
        {
            return symbol != null &&
                   symbol.Family != null &&
                   symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased &&
                   IsLevelBasedFurnitureEquipmentFixtureCategory(symbol);
        }

        private static bool IsWallHostedDoorWindowCategory(FamilySymbol symbol)
        {
            return IsBuiltInCategory(symbol, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows);
        }

        private static bool IsLevelBasedFurnitureEquipmentFixtureCategory(FamilySymbol symbol)
        {
            return IsBuiltInCategory(
                symbol,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_FurnitureSystems,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_SpecialityEquipment);
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

        private static string GetRoomNumber(Room room)
        {
            try
            {
                return room?.Number ?? string.Empty;
            }
            catch
            {
                Parameter parameter = room?.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                return parameter?.AsString() ?? string.Empty;
            }
        }

        private static string GetRoomName(Room room)
        {
            try
            {
                Parameter parameter = room?.get_Parameter(BuiltInParameter.ROOM_NAME);
                string parameterValue = parameter?.AsString();
                if (!string.IsNullOrWhiteSpace(parameterValue)) return parameterValue;
            }
            catch
            {
                // Fall through to the Revit display name.
            }

            return room == null ? string.Empty : SafeElementName(room);
        }

        private static string GetRoomDepartment(Room room)
        {
            try
            {
                Parameter parameter = room?.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                return parameter?.AsString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetRoomLevelName(Document document, Room room)
        {
            ElementId levelId = GetLevelId(room);
            Element level = IsValidElementId(levelId) ? document.GetElement(levelId) : null;
            return level == null ? string.Empty : SafeElementName(level);
        }

        private static ElementId GetCreatedPhaseId(Element element)
        {
            try
            {
                ElementId phaseId = element.CreatedPhaseId;
                return IsValidElementId(phaseId) ? phaseId : ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        private static double SafeRoomArea(Room room)
        {
            try
            {
                return room?.Area ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double SafeRoomVolume(Room room)
        {
            try
            {
                return room?.Volume ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsRoomPlaced(Room room)
        {
            try
            {
                return room != null && room.Location != null;
            }
            catch
            {
                return false;
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

        private static PageResult<T> PageItems<T>(IEnumerable<T> items, int offset, int limit, bool includeTotalCount)
        {
            if (items == null)
            {
                return new PageResult<T>(new List<T>(), includeTotalCount ? 0 : (int?)null, false);
            }

            if (includeTotalCount)
            {
                List<T> materialized = items.ToList();
                int totalCount = materialized.Count;
                List<T> page = materialized.Skip(offset).Take(limit).ToList();
                return new PageResult<T>(page, totalCount, offset + page.Count < totalCount);
            }

            List<T> window = items.Skip(offset).Take(limit + 1).ToList();
            bool truncated = window.Count > limit;
            if (truncated) window.RemoveAt(window.Count - 1);
            return new PageResult<T>(window, null, truncated);
        }

        private sealed class PageResult<T>
        {
            public PageResult(List<T> items, int? totalCount, bool truncated)
            {
                Items = items;
                TotalCount = totalCount;
                Truncated = truncated;
            }

            public List<T> Items { get; }
            public int? TotalCount { get; }
            public bool Truncated { get; }
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? value as Dictionary<string, object> : null;
        }

        private static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            return source == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(source, StringComparer.OrdinalIgnoreCase);
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

        private static long? GetLong(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static double? GetDouble(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> root, string key, bool defaultValue)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return defaultValue;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static bool? GetNullableBool(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static BridgeResponseEnvelope Success(
            BridgeRequestEnvelope request,
            object data,
            Stopwatch sw,
            List<BridgeWarning> warnings = null,
            BridgeMetrics metrics = null,
            long? generation = null)
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
                Generation = generation ?? 0
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
