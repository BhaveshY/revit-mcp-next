using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Revit;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    public static class RevitMcpInProcessBridge
    {
        private static readonly object Gate = new object();
        private static RevitExternalEventHandler _handler;

        internal static void Configure(RevitExternalEventHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (Gate)
            {
                _handler = handler;
            }
        }

        internal static void Clear(RevitExternalEventHandler handler)
        {
            lock (Gate)
            {
                if (ReferenceEquals(_handler, handler))
                {
                    _handler = null;
                }
            }
        }

        public static string StatusJson(UIApplication app)
        {
            return ExecuteEnvelope(
                app,
                new BridgeRequestEnvelope
                {
                    ProtocolVersion = BridgeProtocol.Version,
                    RequestId = Guid.NewGuid().ToString("N"),
                    SessionId = "in-process",
                    Operation = "status",
                    OperationKind = "read",
                    TimeoutMs = 5000,
                    Payload = new Dictionary<string, object>()
                });
        }

        public static string ExecuteJson(UIApplication app, string bridgeRequestJson)
        {
            BridgeRequestEnvelope request = null;
            try
            {
                request = ParseRequest(bridgeRequestJson);
                return ExecuteEnvelope(app, request);
            }
            catch (Exception ex)
            {
                return SerializeResponse(Failure(
                    request ?? new BridgeRequestEnvelope { RequestId = Guid.NewGuid().ToString("N") },
                    "IN_PROCESS_BRIDGE_FAILED",
                    ex.Message,
                    "Pass a valid bridge request JSON object and call from an active Revit API context."));
            }
        }

        private static string ExecuteEnvelope(UIApplication app, BridgeRequestEnvelope request)
        {
            if (app == null)
            {
                return SerializeResponse(Failure(
                    request,
                    "NO_UI_APPLICATION",
                    "A Revit UIApplication is required for in-process pyRevit/Dynamo calls.",
                    "Pass pyRevit __revit__ or Dynamo DocumentManager.Instance.CurrentUIApplication."));
            }

            RevitExternalEventHandler handler;
            lock (Gate)
            {
                handler = _handler;
            }

            if (handler == null)
            {
                return SerializeResponse(Failure(
                    request,
                    "ADDIN_NOT_READY",
                    "Revit MCP Next is not configured in this Revit session.",
                    "Confirm the Revit MCP Next add-in loaded successfully before calling the in-process bridge."));
            }

            BridgeResponseEnvelope response = handler.HandleDirect(app, request);
            return SerializeResponse(response);
        }

        private static BridgeRequestEnvelope ParseRequest(string requestJson)
        {
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                throw new InvalidDataException("Bridge request JSON cannot be empty.");
            }

            object parsed = CreateSerializer().DeserializeObject(requestJson);
            var root = parsed as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidDataException("Bridge request must be a JSON object.");
            }

            string requestId = GetString(root, "requestId");
            string operation = GetString(root, "operation");
            if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidDataException("Bridge request is missing requestId.");
            if (string.IsNullOrWhiteSpace(operation)) throw new InvalidDataException("Bridge request is missing operation.");

            return new BridgeRequestEnvelope
            {
                ProtocolVersion = GetString(root, "protocolVersion") ?? BridgeProtocol.Version,
                RequestId = requestId,
                SessionId = GetString(root, "sessionId") ?? "in-process",
                AuthToken = GetString(root, "authToken"),
                Operation = operation,
                OperationKind = GetString(root, "operationKind") ?? "read",
                TimeoutMs = GetInt(root, "timeoutMs") ?? 30000,
                DocumentFingerprint = GetString(root, "documentFingerprint"),
                ExpectedGeneration = GetLong(root, "expectedGeneration"),
                Payload = GetDictionary(root, "payload") ?? new Dictionary<string, object>()
            };
        }

        private static string SerializeResponse(BridgeResponseEnvelope response)
        {
            var body = new Dictionary<string, object>
            {
                ["ok"] = response.Ok,
                ["requestId"] = response.RequestId,
                ["warnings"] = response.Warnings.Select(ToWireWarning).ToArray(),
                ["metrics"] = ToWireMetrics(response.Metrics)
            };

            if (response.Ok)
            {
                body["data"] = response.Data ?? new Dictionary<string, object>();
                if (response.Generation.HasValue) body["generation"] = response.Generation.Value;
            }
            else
            {
                body["error"] = ToWireError(response.Error);
            }

            return CreateSerializer().Serialize(body);
        }

        private static BridgeResponseEnvelope Failure(
            BridgeRequestEnvelope request,
            string code,
            string message,
            string suggestedNextAction = null)
        {
            return new BridgeResponseEnvelope
            {
                Ok = false,
                RequestId = request?.RequestId ?? string.Empty,
                Error = new BridgeError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true,
                    SuggestedNextAction = suggestedNextAction
                },
                Warnings = new List<BridgeWarning>(),
                Metrics = new BridgeMetrics { ElapsedMs = 0 }
            };
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = 4 * 1024 * 1024,
                RecursionLimit = 64
            };
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            return root.TryGetValue(key, out object value) ? value as Dictionary<string, object> : null;
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            return root.TryGetValue(key, out object value) ? Convert.ToString(value) : null;
        }

        private static int? GetInt(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt32(value);
        }

        private static long? GetLong(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt64(value);
        }

        private static Dictionary<string, object> ToWireWarning(BridgeWarning warning)
        {
            return new Dictionary<string, object>
            {
                ["code"] = warning.Code,
                ["message"] = warning.Message
            };
        }

        private static Dictionary<string, object> ToWireError(BridgeError error)
        {
            if (error == null)
            {
                return new Dictionary<string, object>
                {
                    ["code"] = "UNKNOWN_ERROR",
                    ["message"] = "The Revit add-in failed without error details.",
                    ["recoverable"] = true
                };
            }

            var body = new Dictionary<string, object>
            {
                ["code"] = error.Code,
                ["message"] = error.Message,
                ["recoverable"] = error.Recoverable
            };
            if (!string.IsNullOrWhiteSpace(error.SuggestedNextAction))
            {
                body["suggestedNextAction"] = error.SuggestedNextAction;
            }

            return body;
        }

        private static Dictionary<string, object> ToWireMetrics(BridgeMetrics metrics)
        {
            var body = new Dictionary<string, object>
            {
                ["elapsedMs"] = metrics?.ElapsedMs ?? 0
            };
            if (metrics?.CollectorElapsedMs != null) body["collectorElapsedMs"] = metrics.CollectorElapsedMs.Value;
            if (metrics?.CacheHit != null) body["cacheHit"] = metrics.CacheHit.Value;
            if (metrics?.ReturnedCount != null) body["returnedCount"] = metrics.ReturnedCount.Value;
            if (metrics?.TotalCount != null) body["totalCount"] = metrics.TotalCount.Value;
            return body;
        }
    }
}

