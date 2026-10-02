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
    /// <summary>
    /// The ExternalEvent handler: drains the request queue on the Revit UI thread (SPEC §9.1). ExecuteBatch runs at
    /// most 16 items / 100 ms, then one job step, then refreshes the document snapshot; deferred completions block
    /// later items until they finish. The other partial files hold the legacy per-op helpers that the wave-2 lanes
    /// rewrite (they are no longer dispatched; see OperationRegistry).
    /// </summary>
    internal sealed partial class RevitExternalEventHandler : IExternalEventHandler
    {
        private const int MaxItemsPerExternalEvent = 16;
        private const int MaxExternalEventElapsedMs = 100;

        // Limits used by the legacy helpers in the other partial files.
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

        private readonly RevitRequestQueue _queue;
        private readonly ModelDeliveryWorkflow _modelDelivery;
        private readonly string _runtimeInstanceId;
        private volatile bool _inExecute;

        private sealed class TargetResolutionException : Exception
        {
            public TargetResolutionException(string code, string message)
                : base(message)
            {
                Code = code;
            }

            public string Code { get; }
        }

        public RevitExternalEventHandler(RevitRequestQueue queue)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _runtimeInstanceId = McpRuntime.Instance?.InstanceId ?? "in-process";
            _modelDelivery = new ModelDeliveryWorkflow(_runtimeInstanceId, document => McpRuntime.Registry?.GetGeneration(document) ?? 0);
        }

        /// <summary>True while ExecuteBatch runs on the UI thread (the pump does not need to wake Revit then).</summary>
        public bool InExecute => _inExecute;

        public void Execute(UIApplication app)
        {
            ExecuteBatch(app, "externalEvent", MaxItemsPerExternalEvent, MaxExternalEventElapsedMs, null);
        }

        public string GetName()
        {
            return "Revit MCP Next External Event Handler";
        }

        /// <summary>
        /// Runs queued work (UI thread, API context). <paramref name="filter"/> restricts which items/jobs may run
        /// (the Idling fallback passes idle-safe ops only). Never throws.
        /// </summary>
        public void ExecuteBatch(UIApplication app, string via, int maxItems, int budgetMs, Func<OpMeta, bool> filter)
        {
            if (_inExecute) return;
            _inExecute = true;
            var elapsed = Stopwatch.StartNew();
            try
            {
                try
                {
                    McpRuntime.UiMonitor?.RefreshHandle(app.MainWindowHandle);
                }
                catch
                {
                    // The handle is advisory.
                }

                if (!CompleteDeferred()) return;

                int processed = 0;
                Func<QueuedRevitWorkItem, bool> itemFilter = null;
                if (filter != null) itemFilter = item => filter(McpRuntime.Ops?.GetMeta(item.Op) ?? new OpMeta { Idle = false });
                while (processed < maxItems && _queue.TryDequeue(out QueuedRevitWorkItem item, itemFilter))
                {
                    McpRuntime.Pump?.OnExecuteStart(processed == 0 ? item.RaiseToExecMs : -1);
                    processed++;
                    RunItem(app, item, via);
                    if (_queue.Deferred != null) return;
                    if (elapsed.ElapsedMilliseconds >= budgetMs) break;
                }

                Func<JobRecord, bool> jobFilter = null;
                if (filter != null) jobFilter = job => filter(McpRuntime.Ops?.GetMeta(job.Key) ?? new OpMeta { Idle = false });
                McpRuntime.Jobs?.RunNext(app, jobFilter);

                try
                {
                    McpRuntime.Registry?.RefreshActive(app);
                    McpRuntime.Registry?.SampleSelection(app);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Debug("execute", "Snapshot refresh failed: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("execute", "ExecuteBatch failed.", ex);
            }
            finally
            {
                _inExecute = false;
                // Re-raise only for work that can run right away; deferred completions and delayed job steps are
                // picked up by the pump's watchdog (wake.watchdogMs) instead of a tight Execute loop.
                bool runnable = _queue.Deferred == null && (_queue.HasPending || (McpRuntime.Jobs?.HasRunnableStep ?? false));
                if (runnable) McpRuntime.Pump?.Raise();
            }
        }

        private void RunItem(UIApplication app, QueuedRevitWorkItem item, string via)
        {
            _queue.BeginExecution(item, new ExecutingInfo
            {
                RequestId = item.RequestId,
                Op = item.Op,
                SinceUtc = DocumentRegistry.Utc(DateTime.UtcNow),
                ClientKey = item.ClientKey,
                WriteTag = item.WriteTag
            });
            bool deferred = false;
            try
            {
                BridgeResponse response = Dispatcher.Execute(item, app, via);
                if (response != null)
                {
                    item.TrySetResult(response);
                }
                else if (item.DeferredTask != null)
                {
                    deferred = true;
                    _queue.SetDeferred(item);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("execute", "Request " + item.RequestId + " (" + item.Op + ") failed outside the dispatcher.", ex);
                item.TrySetResult(BridgeResponse.Failure(item.RequestId, ErrorCodes.InternalError, ex.Message,
                    new Dictionary<string, object> { ["requestId"] = item.RequestId, ["logPath"] = DiagnosticsLogger.CurrentLogFile }));
            }
            finally
            {
                if (!deferred) _queue.EndExecution();
            }
        }

        /// <summary>Finishes a deferred item when its task completed or timed out. Returns false while it must keep waiting.</summary>
        private bool CompleteDeferred()
        {
            QueuedRevitWorkItem item = _queue.Deferred;
            if (item == null) return true;
            RequestContext ctx = item.Context;
            OpResult result = null;
            if (item.DeferredTask.IsCompleted)
            {
                if (item.DeferredTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                {
                    result = item.DeferredTask.Result ?? OpResult.Success();
                }
                else
                {
                    Exception error = item.DeferredTask.Exception?.GetBaseException() ?? new OperationCanceledException();
                    result = Dispatcher.MapException(ctx, error);
                }
            }
            else if (DateTime.UtcNow >= item.DeferredDeadlineUtc)
            {
                try
                {
                    result = item.DeferredOnTimeout?.Invoke() ??
                             OpResult.Fail(ErrorCodes.InternalError, item.Op + " did not complete within its deferral timeout.");
                }
                catch (Exception ex)
                {
                    result = Dispatcher.MapException(ctx, ex);
                }
            }
            if (result == null) return false;

            RequestContext previous = RequestContext.Current;
            try
            {
                RequestContext.Current = ctx;
                item.TrySetResult(Dispatcher.BuildResponse(ctx, result, item.Request));
            }
            finally
            {
                RequestContext.Current = previous;
                _queue.ClearDeferred(item);
                _queue.EndExecution();
            }
            return true;
        }

        /// <summary>Legacy helper kept for the old per-op functions: fails when an expected generation is stale.</summary>
        private LegacyResponse ValidateExpectedGeneration(
            LegacyRequest request,
            Document document,
            Stopwatch sw,
            out long generation)
        {
            generation = McpRuntime.Registry?.GetGeneration(document) ?? 0;
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
    }
}
