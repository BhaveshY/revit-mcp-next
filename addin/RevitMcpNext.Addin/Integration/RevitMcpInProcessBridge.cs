using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// In-process entry for pyRevit/Dynamo (SPEC §9.3, §16): protocol v3 request JSON in, response JSON out, executed
    /// synchronously on the caller's Revit API thread through the normal dispatch pipeline, with the add-in's shared
    /// DocumentRegistry and OperationRegistry. Rules:
    /// <list type="bullet">
    /// <item>Only registry keys with <c>inproc:true</c> are reachable. Destructive, lifecycle and code ops are refused with
    /// UNSUPPORTED_OP (reason inproc) before anything resolves; the dispatcher checks the flag again.</item>
    /// <item>The caller must be on the Revit UI thread (a pyRevit command, a Dynamo node, an ExternalEvent); other threads
    /// get INVALID_ARGS (param thread) and nothing runs.</item>
    /// <item>Control ops (health, snapshot, dialogs, ...) are answered directly; press is refused (it needs the user's OK
    /// through the broker's confirm flow).</item>
    /// </list>
    /// No auth: the caller already runs inside Revit. Every call is logged with its key and outcome.
    /// </summary>
    public static class RevitMcpInProcessBridge
    {
        private static int _uiThreadId;

        /// <summary>Records the Revit UI thread (RevitMcpApplication.OnStartup).</summary>
        internal static void SetUiThread(int managedThreadId)
        {
            Interlocked.Exchange(ref _uiThreadId, managedThreadId);
        }

        /// <summary>True when the current thread is the Revit UI thread (unknown counts as true before startup ran).</summary>
        internal static bool OnUiThread
        {
            get
            {
                int ui = Volatile.Read(ref _uiThreadId);
                return ui == 0 || ui == Thread.CurrentThread.ManagedThreadId;
            }
        }

        /// <summary>The add-in's hello data plus the document snapshot (no Revit API work).</summary>
        public static string StatusJson(UIApplication app)
        {
            try
            {
                var control = new ControlOperations(null);
                var data = new Dictionary<string, object>
                {
                    ["hello"] = control.Hello(),
                    ["snapshot"] = McpRuntime.Registry?.Current
                };
                return NamedPipeHost.SerializeResponse(BridgeResponse.Success(Guid.NewGuid().ToString("N"), data, "in-process status"));
            }
            catch (Exception ex)
            {
                return NamedPipeHost.SerializeResponse(BridgeResponse.Failure(null, ErrorCodes.InternalError, ex.Message));
            }
        }

        /// <summary>
        /// Executes one v3 request ({op, kind, args, doc?, mode?}). "v" may be omitted; requestId defaults to a new id and
        /// kind to the registry kind. Must be called from a valid Revit API context (pyRevit command, Dynamo node,
        /// ExternalEvent).
        /// </summary>
        public static string ExecuteJson(UIApplication app, string bridgeRequestJson)
        {
            BridgeRequest request;
            try
            {
                if (string.IsNullOrWhiteSpace(bridgeRequestJson)) throw new InvalidDataException("The request JSON is empty.");
                if (!(JsonWireCodec.DeserializeObject(bridgeRequestJson) is IDictionary<string, object> root))
                {
                    throw new InvalidDataException("The request must be a JSON object.");
                }
                request = BridgeRequest.FromWire(root);
            }
            catch (Exception ex)
            {
                return NamedPipeHost.SerializeResponse(BridgeResponse.Failure(null, ErrorCodes.InvalidArgs,
                    "Invalid in-process request: " + ex.Message,
                    new Dictionary<string, object> { ["param"] = "request", ["reason"] = ex.Message }));
            }
            return NamedPipeHost.SerializeResponse(Execute(app, request));
        }

        /// <summary>Executes a parsed request (also used by the e2e test op test.inproc_call). Never throws.</summary>
        internal static BridgeResponse Execute(UIApplication app, BridgeRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            BridgeResponse response;
            try
            {
                response = ExecuteCore(app, request);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("inproc", "In-process request " + request?.Op + " failed.", ex);
                response = BridgeResponse.Failure(request?.RequestId, ErrorCodes.InternalError, ex.Message,
                    new Dictionary<string, object> { ["requestId"] = request?.RequestId, ["logPath"] = DiagnosticsLogger.CurrentLogFile });
            }
            if (response.Metrics == null) response.Metrics = new ResponseMetrics();
            response.Metrics.Via = "inProcess";
            if (response.Metrics.ExecMs == 0) response.Metrics.ExecMs = stopwatch.ElapsedMilliseconds;
            DiagnosticsLogger.Event("inproc", new Dictionary<string, object>
            {
                ["reqId"] = request?.RequestId,
                ["op"] = request?.Op,
                ["clientKey"] = request?.ClientKey,
                ["execMs"] = stopwatch.ElapsedMilliseconds,
                ["code"] = response.Ok ? null : response.Code
            });
            return response;
        }

        private static BridgeResponse ExecuteCore(UIApplication app, BridgeRequest request)
        {
            if (request == null)
            {
                return BridgeResponse.Failure(null, ErrorCodes.InvalidArgs, "The request is empty.",
                    new Dictionary<string, object> { ["param"] = "request", ["reason"] = "required" });
            }
            if (string.IsNullOrWhiteSpace(request.V)) request.V = BridgeProtocol.Version;
            if (string.IsNullOrWhiteSpace(request.RequestId)) request.RequestId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(request.ClientKey)) request.ClientKey = "in-process";
            if (string.IsNullOrWhiteSpace(request.Op))
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.InvalidArgs, "The request has no op.",
                    new Dictionary<string, object> { ["param"] = "op", ["reason"] = "required" });
            }

            BridgeResponse protocolError = BridgeProtocolGuard.Check(request, McpRuntime.Instance?.AddinVersion);
            if (protocolError != null) return protocolError;

            if (!OnUiThread)
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.InvalidArgs,
                    "The in-process bridge must be called on the Revit UI thread (a pyRevit command, a Dynamo node or an ExternalEvent).",
                    new Dictionary<string, object> { ["param"] = "thread", ["reason"] = "not the Revit UI thread" });
            }
            if (app == null)
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.InvalidArgs,
                    "A Revit UIApplication is required (pyRevit __revit__ or Dynamo DocumentManager.Instance.CurrentUIApplication).",
                    new Dictionary<string, object> { ["param"] = "app", ["reason"] = "required" });
            }
            OperationRegistry ops = McpRuntime.Ops;
            if (ops == null)
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.RevitStarting, "Revit MCP Next is not running in this Revit session.");
            }

            if (ControlOps.IsControlOp(request.Op))
            {
                if (request.Op == ControlOps.Press)
                {
                    return Refused(request, "pressing dialog buttons needs the user's OK through the MCP broker");
                }
                return new ControlOperations(null).Handle(request);
            }

            // inproc:false keys are refused before any document or handler lookup (C37: destructive, lifecycle, code).
            OpMeta meta = ops.GetMeta(request.Op);
            if (meta != null && !meta.Inproc) return Refused(request, null);
            if (meta != null && string.IsNullOrWhiteSpace(request.Kind)) request.Kind = meta.Kind;

            return Dispatcher.InvokeInProcess(app, request);
        }

        private static BridgeResponse Refused(BridgeRequest request, string reason)
        {
            return BridgeResponse.Failure(request.RequestId, ErrorCodes.UnsupportedOp,
                "'" + request.Op + "' is not available through the in-process bridge" + (reason == null ? "." : ": " + reason + "."),
                new Dictionary<string, object> { ["reason"] = "inproc", ["key"] = request.Op });
        }
    }
}
