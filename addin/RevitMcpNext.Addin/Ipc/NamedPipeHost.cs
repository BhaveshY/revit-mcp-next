using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using RevitMcpNext.Addin.Revit;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    internal sealed class NamedPipeHost : IDisposable
    {
        private readonly string _pipeName;
        private readonly RevitRequestQueue _requestQueue;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private Task _acceptLoop;

        public NamedPipeHost(string pipeName, RevitRequestQueue requestQueue)
        {
            _pipeName = pipeName;
            _requestQueue = requestQueue;
        }

        public void Start()
        {
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try
            {
                _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Shutdown should not block Revit.
            }
            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        using (server)
                        {
                            await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
                        }
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
        {
            string requestJson = await FramedPipeTransport.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);

            BridgeResponseEnvelope response;
            try
            {
                BridgeRequestEnvelope request = ParseRequest(requestJson);
                if (!string.Equals(request.ProtocolVersion, BridgeProtocol.Version, StringComparison.Ordinal))
                {
                    response = Failure(
                        request,
                        "PROTOCOL_VERSION_MISMATCH",
                        "Broker protocol " + request.ProtocolVersion + " does not match add-in protocol " + BridgeProtocol.Version + ".",
                        "Rebuild and reinstall Revit MCP Next so the broker and add-in use the same version.");
                }
                else
                {
                    response = await _requestQueue.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                response = Failure(
                    new BridgeRequestEnvelope { RequestId = Guid.NewGuid().ToString("N") },
                    "INVALID_BRIDGE_REQUEST",
                    ex.Message,
                    "Restart the MCP client after rebuilding Revit MCP Next.");
            }

            string responseJson = SerializeResponse(response);
            await FramedPipeTransport.WriteFrameAsync(stream, responseJson, cancellationToken).ConfigureAwait(false);
        }

        private static BridgeRequestEnvelope ParseRequest(string requestJson)
        {
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
                SessionId = GetString(root, "sessionId") ?? string.Empty,
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
                RequestId = request.RequestId,
                Error = new BridgeError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true,
                    SuggestedNextAction = suggestedNextAction
                },
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
