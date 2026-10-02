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
    internal sealed partial class RevitExternalEventHandler : IExternalEventHandler
    {
        private const string AddinVersion = "0.3.0";
        private const int MaxItemsPerExternalEvent = 16;
        private const int MaxExternalEventElapsedMs = 100;
        private const int MaxQueryLimit = 500;
        private const int MaxViewLimit = 500;
        private const int MaxSheetLimit = 500;
        private const int MaxScheduleLimit = 500;
        private const int MaxScheduleFieldLimit = 500;
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
                ["create_model_delivery_fixture"] = "write",
                ["get_view_details"] = "read",
                ["get_dimensions"] = "read",
                ["activate_view"] = "write",
                ["get_levels"] = "read",
                ["get_views"] = "read",
                ["get_sheets"] = "read",
                ["get_schedules"] = "read",
                ["get_schedule_fields"] = "read",
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
                ["inspect_model_delivery"] = "read",
                ["preview_model_delivery"] = "preview",
                ["execute_model_delivery"] = "destructive",
                ["get_model_delivery_status"] = "read",
                ["cancel_model_delivery"] = "debug",
                ["preview_change_set"] = "preview",
                ["apply_change_set"] = "write",
                ["cancel_request"] = "debug"
            };
        private readonly RevitRequestQueue _queue;
        private readonly TransactionService _transactions;
        private readonly DocumentGenerationTracker _generations;
        private readonly PreviewTokenStore _previewTokens;
        private readonly ModelDeliveryWorkflow _modelDelivery;
        private readonly string _runtimeInstanceId;

        private sealed class TargetResolutionException : Exception
        {
            public TargetResolutionException(string code, string message)
                : base(message)
            {
                Code = code;
            }

            public string Code { get; }
        }

        public RevitExternalEventHandler(
            RevitRequestQueue queue,
            TransactionService transactions,
            DocumentGenerationTracker generations = null,
            PreviewTokenStore previewTokens = null,
            string runtimeInstanceId = null)
        {
            _queue = queue;
            _transactions = transactions;
            _generations = generations ?? new DocumentGenerationTracker();
            _previewTokens = previewTokens ?? new PreviewTokenStore();
            _runtimeInstanceId = string.IsNullOrWhiteSpace(runtimeInstanceId) ? "in-process" : runtimeInstanceId;
            _modelDelivery = new ModelDeliveryWorkflow(_runtimeInstanceId, document => _generations.GetGeneration(document));
        }

        public void Execute(UIApplication app)
        {
            int processed = 0;
            var elapsed = Stopwatch.StartNew();
            while (processed < MaxItemsPerExternalEvent && _queue.TryDequeue(out QueuedRevitWorkItem item))
            {
                processed++;
                var execution = Stopwatch.StartNew();
                try
                {
                    BridgeResponseEnvelope response = Handle(app, item.Envelope);
                    execution.Stop();
                    item.TrySetResult(response, execution.ElapsedMilliseconds);
                }
                finally
                {
                    _queue.EndExecution();
                }
                if (elapsed.ElapsedMilliseconds >= MaxExternalEventElapsedMs) break;
            }

            if (_modelDelivery.HasPendingWork)
            {
                _queue.BeginExecution(null, "model_delivery_step");
                try
                {
                    _modelDelivery.ProcessNext(app);
                }
                finally
                {
                    _queue.EndExecution();
                }
            }

            if (_queue.HasPending || _modelDelivery.HasPendingWork)
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
                if (!string.IsNullOrWhiteSpace(request.InstanceId) &&
                    !string.Equals(request.InstanceId, _runtimeInstanceId, StringComparison.Ordinal))
                {
                    return Failure(
                        request,
                        "TARGET_INSTANCE_MISMATCH",
                        "This request targets Revit instance " + request.InstanceId +
                        " but reached instance " + _runtimeInstanceId + ". Refresh revit.list_instances before retrying.",
                        sw);
                }

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
                        case "create_model_delivery_fixture":
                            return HandleCreateModelDeliveryFixture(app, request, sw);
                        case "get_levels":
                            return HandleGetLevels(app, request, sw);
                        case "get_view_details":
                        case "get_dimensions":
                        case "activate_view":
                            return HandleViewWorkflow(app, request, sw);
                        case "get_views":
                            return HandleGetViews(app, request, sw);
                        case "get_sheets":
                            return HandleGetSheets(app, request, sw);
                        case "get_schedules":
                            return HandleGetSchedules(app, request, sw);
                        case "get_schedule_fields":
                            return HandleGetScheduleFields(app, request, sw);
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
                        case "inspect_model_delivery":
                            return HandleInspectModelDelivery(app, request, sw);
                        case "preview_model_delivery":
                            return HandlePreviewModelDelivery(app, request, sw);
                        case "execute_model_delivery":
                            return HandleExecuteModelDelivery(app, request, sw);
                        case "get_model_delivery_status":
                            return HandleGetModelDeliveryStatus(request, sw);
                        case "cancel_model_delivery":
                            return HandleCancelModelDelivery(request, sw);
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
            catch (TargetResolutionException ex)
            {
                return Failure(request, ex.Code, ex.Message, sw);
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
    }
}
