using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// In-process entry for pyRevit/Dynamo (SPEC §9.3): protocol v3 request JSON in, response JSON out, executed
    /// synchronously on the caller's Revit API thread through the normal dispatch pipeline. Only registry keys with
    /// inproc:true are reachable (destructive, lifecycle and code ops are refused). No auth: the caller already runs
    /// inside Revit.
    /// </summary>
    public static class RevitMcpInProcessBridge
    {
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
        /// Executes one v3 request ({op, kind, args, doc?, mode?}). "v" may be omitted; requestId defaults to a new id.
        /// Must be called from a valid Revit API context (pyRevit command, Dynamo node, ExternalEvent).
        /// </summary>
        public static string ExecuteJson(UIApplication app, string bridgeRequestJson)
        {
            BridgeRequest request = null;
            try
            {
                if (string.IsNullOrWhiteSpace(bridgeRequestJson)) throw new InvalidDataException("The request JSON is empty.");
                if (!(JsonWireCodec.DeserializeObject(bridgeRequestJson) is IDictionary<string, object> root))
                {
                    throw new InvalidDataException("The request must be a JSON object.");
                }
                request = BridgeRequest.FromWire(root);
                if (string.IsNullOrWhiteSpace(request.V)) request.V = BridgeProtocol.Version;
                if (string.IsNullOrWhiteSpace(request.RequestId)) request.RequestId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(request.ClientKey)) request.ClientKey = "in-process";

                BridgeResponse protocolError = BridgeProtocolGuard.Check(request, McpRuntime.Instance?.AddinVersion);
                if (protocolError != null) return NamedPipeHost.SerializeResponse(protocolError);
                if (app == null)
                {
                    return NamedPipeHost.SerializeResponse(BridgeResponse.Failure(request.RequestId, ErrorCodes.InvalidArgs,
                        "A Revit UIApplication is required (pyRevit __revit__ or Dynamo DocumentManager.Instance.CurrentUIApplication).",
                        new Dictionary<string, object> { ["param"] = "app", ["reason"] = "required" }));
                }
                if (McpRuntime.Ops == null)
                {
                    return NamedPipeHost.SerializeResponse(BridgeResponse.Failure(request.RequestId, ErrorCodes.RevitStarting,
                        "Revit MCP Next is not running in this Revit session."));
                }
                if (ControlOps.IsControlOp(request.Op))
                {
                    return NamedPipeHost.SerializeResponse(new ControlOperations(null).Handle(request));
                }
                return NamedPipeHost.SerializeResponse(Dispatcher.InvokeInProcess(app, request));
            }
            catch (Exception ex)
            {
                return NamedPipeHost.SerializeResponse(BridgeResponse.Failure(request?.RequestId, ErrorCodes.InvalidArgs,
                    "Invalid in-process request: " + ex.Message,
                    new Dictionary<string, object> { ["param"] = "request", ["reason"] = ex.Message }));
            }
        }
    }
}
