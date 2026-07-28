using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Addin.Revit;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    internal sealed class NamedPipeHost : IDisposable
    {
        private const int DefaultPipeBufferSize = 0;
        private const int AcceptWorkerCount = 4;
        private const int HandshakeTimeoutMs = 10000;
        private const int ResponseWriteTimeoutMs = 5000;
        private const int InitialAcceptRetryDelayMs = 100;
        private const int MaxAcceptRetryDelayMs = 2000;
        private const int ShutdownWaitMs = 5000;

        private readonly string _pipeName;
        private readonly RevitRequestQueue _requestQueue;
        private readonly PipeAuthOptions _authOptions;
#if NETFRAMEWORK
        private readonly PipeSecurity _pipeSecurity;
#endif
        private readonly Func<NamedPipeServerStream> _serverFactory;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _lifecycleGate = new object();
        private readonly NamedPipeServerStream[] _workerStreams = new NamedPipeServerStream[AcceptWorkerCount];
        private Task[] _acceptWorkers = Array.Empty<Task>();
        private int _started;
        private int _disposed;
        private int _waitingListenerCount;
        private int _activeConnectionCount;
        private long _acceptedConnectionCount;
        private long _completedConnectionCount;
        private long _clientFaultCount;
        private long _listenerFaultCount;
        private long _lastAcceptedUtcTicks;
        private long _lastFaultUtcTicks;

        public NamedPipeHost(string pipeName, RevitRequestQueue requestQueue, PipeAuthOptions authOptions = null)
            : this(pipeName, requestQueue, authOptions, null)
        {
        }

        internal NamedPipeHost(
            string pipeName,
            RevitRequestQueue requestQueue,
            PipeAuthOptions authOptions,
            Func<NamedPipeServerStream> serverFactory)
        {
            _pipeName = pipeName;
            _requestQueue = requestQueue;
            _authOptions = authOptions ?? PipeAuthOptions.FromEnvironment();
#if NETFRAMEWORK
            _pipeSecurity = PipeSecurityFactory.CreateCurrentUserOnly();
#endif
            _serverFactory = serverFactory;
        }

        public void Start()
        {
            NamedPipeServerStream[] initialStreams = new NamedPipeServerStream[AcceptWorkerCount];
            lock (_lifecycleGate)
            {
                if (_disposed != 0)
                {
                    throw new ObjectDisposedException(nameof(NamedPipeHost));
                }
                if (_started != 0)
                {
                    throw new InvalidOperationException("The named pipe host has already been started.");
                }

                try
                {
                    for (int workerId = 0; workerId < AcceptWorkerCount; workerId++)
                    {
                        initialStreams[workerId] = CreateServerStream();
                        _workerStreams[workerId] = initialStreams[workerId];
                    }
                }
                catch
                {
                    for (int workerId = 0; workerId < initialStreams.Length; workerId++)
                    {
                        DisposeStream(initialStreams[workerId]);
                        _workerStreams[workerId] = null;
                    }
                    throw;
                }

                _started = 1;
                _acceptWorkers = initialStreams
                    .Select((stream, workerId) => Task.Run(
                        () => AcceptWorkerAsync(workerId, stream, _shutdown.Token)))
                    .ToArray();
            }

            DiagnosticsLogger.Info(
                "Named pipe host started with " + AcceptWorkerCount.ToString(CultureInfo.InvariantCulture) +
                " bounded accept workers. " + DescribeHealth());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _shutdown.Cancel();
            Task[] workers;
            NamedPipeServerStream[] streams;
            lock (_lifecycleGate)
            {
                workers = _acceptWorkers.ToArray();
                streams = _workerStreams.ToArray();
                for (int workerId = 0; workerId < _workerStreams.Length; workerId++)
                {
                    _workerStreams[workerId] = null;
                }
            }

            foreach (NamedPipeServerStream stream in streams)
            {
                DisposeStream(stream);
            }

            try
            {
                if (workers.Length > 0 &&
                    !Task.WaitAll(workers, TimeSpan.FromMilliseconds(ShutdownWaitMs)))
                {
                    DiagnosticsLogger.Error(
                        "Timed out waiting for named pipe accept workers to stop. " + DescribeHealth());
                }
            }
            catch (AggregateException ex)
            {
                Exception unexpected = ex.Flatten().InnerExceptions.FirstOrDefault(
                    error => !(error is OperationCanceledException) && !(error is ObjectDisposedException));
                if (unexpected != null)
                {
                    DiagnosticsLogger.Error(
                        "Named pipe accept workers faulted during shutdown. " + DescribeHealth(),
                        unexpected);
                }
            }

            DiagnosticsLogger.Info("Named pipe host stopped. " + DescribeHealth());
            _shutdown.Dispose();
        }

        private async Task AcceptWorkerAsync(
            int workerId,
            NamedPipeServerStream initialStream,
            CancellationToken cancellationToken)
        {
            NamedPipeServerStream server = initialStream;
            int consecutiveAcceptFaults = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (server == null)
                {
                    try
                    {
                        server = CreateServerStream();
                        if (!RegisterWorkerStream(workerId, server, cancellationToken))
                        {
                            DisposeStream(server);
                            return;
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        RecordListenerFault(workerId, "creating a server stream", ex);
                        consecutiveAcceptFaults++;
                        if (!await DelayAfterAcceptFaultAsync(consecutiveAcceptFaults, cancellationToken)
                            .ConfigureAwait(false))
                        {
                            return;
                        }
                        continue;
                    }
                }

                try
                {
                    Interlocked.Increment(ref _waitingListenerCount);
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    consecutiveAcceptFaults = 0;
                    Interlocked.Increment(ref _acceptedConnectionCount);
                    Interlocked.Exchange(ref _lastAcceptedUtcTicks, DateTime.UtcNow.Ticks);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    ClearWorkerStream(workerId, server);
                    DisposeStream(server);
                    server = null;
                    RecordListenerFault(workerId, "waiting for a client", ex);
                    consecutiveAcceptFaults++;
                    if (!await DelayAfterAcceptFaultAsync(consecutiveAcceptFaults, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }
                finally
                {
                    Interlocked.Decrement(ref _waitingListenerCount);
                }

                try
                {
                    await HandleClientSafelyAsync(server, workerId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    RecordClientFault(
                        workerId,
                        null,
                        null,
                        "escaped the guarded client handler",
                        ex);
                }
                finally
                {
                    ClearWorkerStream(workerId, server);
                    DisposeStream(server);
                    server = null;
                }
            }
        }

        private async Task<bool> DelayAfterAcceptFaultAsync(
            int consecutiveAcceptFaults,
            CancellationToken cancellationToken)
        {
            int exponent = Math.Min(4, Math.Max(0, consecutiveAcceptFaults - 1));
            int delayMs = Math.Min(
                MaxAcceptRetryDelayMs,
                InitialAcceptRetryDelayMs * (1 << exponent));
            try
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        private async Task HandleClientSafelyAsync(
            NamedPipeServerStream stream,
            int workerId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeConnectionCount);
            string requestId = null;
            string operation = null;
            try
            {
                string requestJson;
                Task<string> readTask = FramedPipeTransport.ReadFrameAsync(stream, cancellationToken);
                using (var handshakeDeadlineCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Task handshakeDeadline = Task.Delay(
                        HandshakeTimeoutMs,
                        handshakeDeadlineCancellation.Token);
                    Task completedHandshake = await Task.WhenAny(readTask, handshakeDeadline).ConfigureAwait(false);
                    if (!ReferenceEquals(completedHandshake, readTask))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        DisposeStream(stream);
                        try
                        {
                            await readTask.ConfigureAwait(false);
                        }
                        catch
                        {
                            // Disposing the stream is how net48 reliably interrupts a pending pipe read.
                        }

                        RecordClientFault(
                            workerId,
                            requestId,
                            operation,
                            "timed out while reading the request frame",
                            new TimeoutException(
                                "The client did not send a complete bridge request frame within " +
                                HandshakeTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms."));
                        return;
                    }

                    handshakeDeadlineCancellation.Cancel();
                }
                requestJson = await readTask.ConfigureAwait(false);

                BridgeResponseEnvelope response;
                try
                {
                    ParsedBridgeRequest parsedRequest = ParseRequest(requestJson);
                    BridgeRequestEnvelope request = parsedRequest.Request;
                    requestId = request.RequestId;
                    operation = request.Operation;
                    if (!_authOptions.IsAuthorized(parsedRequest.AuthToken))
                    {
                        response = Failure(
                            request,
                            "AUTHENTICATION_FAILED",
                            "Bridge request authentication failed.",
                            "Set REVIT_MCP_NEXT_AUTH_TOKEN for the broker to the same value configured for the Revit add-in.");
                    }
                    else if (!string.Equals(request.ProtocolVersion, BridgeProtocol.Version, StringComparison.Ordinal))
                    {
                        response = Failure(
                            request,
                            "PROTOCOL_VERSION_MISMATCH",
                            "Broker protocol " + request.ProtocolVersion + " does not match add-in protocol " + BridgeProtocol.Version + ".",
                            "Rebuild and reinstall Revit MCP Next so the broker and add-in use the same version.");
                    }
                    else
                    {
                        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            requestTimeout.CancelAfter(ComputeServerQueueTimeoutMs(request.TimeoutMs));
                            response = await _requestQueue.EnqueueAsync(request, requestTimeout.Token).ConfigureAwait(false);
                            if (requestTimeout.IsCancellationRequested &&
                                response?.Error != null &&
                                string.Equals(response.Error.Code, "REQUEST_CANCELLED", StringComparison.Ordinal))
                            {
                                response = Failure(
                                    request,
                                    "REVIT_EXTERNAL_EVENT_TIMEOUT",
                                    "The named pipe accepted the request, but Revit did not process the ExternalEvent before the bridge timeout. Revit may be busy or blocked by a modal dialog.",
                                    "Bring Revit to the foreground and close any modal dialogs, then retry. If this happened during smoke, inspect the Revit journal for TaskDialog entries.");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordClientFault(workerId, requestId, operation, "failed to process the bridge request", ex);
                    response = Failure(
                        new BridgeRequestEnvelope { RequestId = requestId ?? Guid.NewGuid().ToString("N") },
                        "INVALID_BRIDGE_REQUEST",
                        ex.Message,
                        "Restart the MCP client after rebuilding Revit MCP Next.");
                }

                string responseJson = SerializeResponse(response);
                Task writeTask = FramedPipeTransport.WriteFrameAsync(stream, responseJson, cancellationToken);
                using (var responseDeadlineCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Task responseDeadline = Task.Delay(
                        ResponseWriteTimeoutMs,
                        responseDeadlineCancellation.Token);
                    Task completedWrite = await Task.WhenAny(writeTask, responseDeadline).ConfigureAwait(false);
                    if (!ReferenceEquals(completedWrite, writeTask))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        DisposeStream(stream);
                        try
                        {
                            await writeTask.ConfigureAwait(false);
                        }
                        catch
                        {
                            // Disposing the stream is how net48 reliably interrupts a pending pipe write.
                        }

                        RecordClientFault(
                            workerId,
                            requestId,
                            operation,
                            "timed out while writing the response frame",
                            new TimeoutException(
                                "The client did not read the bridge response frame within " +
                                ResponseWriteTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms."));
                        return;
                    }

                    responseDeadlineCancellation.Cancel();
                }
                await writeTask.ConfigureAwait(false);

                Interlocked.Increment(ref _completedConnectionCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                RecordClientFault(workerId, requestId, operation, "client connection failed", ex);
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnectionCount);
            }
        }

        private bool RegisterWorkerStream(
            int workerId,
            NamedPipeServerStream stream,
            CancellationToken cancellationToken)
        {
            lock (_lifecycleGate)
            {
                if (_disposed != 0 || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                _workerStreams[workerId] = stream;
                return true;
            }
        }

        private void ClearWorkerStream(int workerId, NamedPipeServerStream stream)
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_workerStreams[workerId], stream))
                {
                    _workerStreams[workerId] = null;
                }
            }
        }

        private void RecordListenerFault(int workerId, string phase, Exception exception)
        {
            Interlocked.Increment(ref _listenerFaultCount);
            Interlocked.Exchange(ref _lastFaultUtcTicks, DateTime.UtcNow.Ticks);
            DiagnosticsLogger.Error(
                "Named pipe accept worker " + workerId.ToString(CultureInfo.InvariantCulture) +
                " failed while " + phase + "; it will retry. " + DescribeHealth(),
                exception);
        }

        private void RecordClientFault(
            int workerId,
            string requestId,
            string operation,
            string phase,
            Exception exception)
        {
            Interlocked.Increment(ref _clientFaultCount);
            Interlocked.Exchange(ref _lastFaultUtcTicks, DateTime.UtcNow.Ticks);
            DiagnosticsLogger.Error(
                "Named pipe client on worker " + workerId.ToString(CultureInfo.InvariantCulture) +
                " " + phase +
                ". requestId=" + (string.IsNullOrWhiteSpace(requestId) ? "(unparsed)" : requestId) +
                " operation=" + (string.IsNullOrWhiteSpace(operation) ? "(unparsed)" : operation) +
                ". " + DescribeHealth(),
                exception);
        }

        private string DescribeHealth()
        {
            return
                "pipe=" + _pipeName +
                " waitingListeners=" + Volatile.Read(ref _waitingListenerCount).ToString(CultureInfo.InvariantCulture) +
                " activeConnections=" + Volatile.Read(ref _activeConnectionCount).ToString(CultureInfo.InvariantCulture) +
                " accepted=" + Interlocked.Read(ref _acceptedConnectionCount).ToString(CultureInfo.InvariantCulture) +
                " completed=" + Interlocked.Read(ref _completedConnectionCount).ToString(CultureInfo.InvariantCulture) +
                " clientFaults=" + Interlocked.Read(ref _clientFaultCount).ToString(CultureInfo.InvariantCulture) +
                " listenerFaults=" + Interlocked.Read(ref _listenerFaultCount).ToString(CultureInfo.InvariantCulture) +
                " lastAcceptedAtUtc=" + FormatTimestamp(Interlocked.Read(ref _lastAcceptedUtcTicks)) +
                " lastFaultAtUtc=" + FormatTimestamp(Interlocked.Read(ref _lastFaultUtcTicks)) + ".";
        }

        private static string FormatTimestamp(long utcTicks)
        {
            return utcTicks <= 0
                ? "(none)"
                : new DateTime(utcTicks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        }

        private static void DisposeStream(NamedPipeServerStream stream)
        {
            if (stream == null) return;
            try
            {
                stream.Dispose();
            }
            catch
            {
                // Stream disposal is best effort during fault recovery and Revit shutdown.
            }
        }

        private static int ComputeServerQueueTimeoutMs(int clientTimeoutMs)
        {
            int normalizedTimeout = Math.Max(1, clientTimeoutMs);
            int margin = Math.Min(1000, Math.Max(250, normalizedTimeout / 10));
            return Math.Max(1, normalizedTimeout - margin);
        }

        private NamedPipeServerStream CreateServerStream()
        {
            if (_serverFactory != null)
            {
                return _serverFactory();
            }

#if NETFRAMEWORK
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                AcceptWorkerCount,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                DefaultPipeBufferSize,
                DefaultPipeBufferSize,
                _pipeSecurity);
#else
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                AcceptWorkerCount,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                DefaultPipeBufferSize,
                DefaultPipeBufferSize);
#endif
        }

        private static ParsedBridgeRequest ParseRequest(string requestJson)
        {
            object parsed = JsonWireCodec.DeserializeObject(requestJson);
            var root = parsed as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidDataException("Bridge request must be a JSON object.");
            }

            string requestId = GetString(root, "requestId");
            string operation = GetString(root, "operation");
            if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidDataException("Bridge request is missing requestId.");
            if (string.IsNullOrWhiteSpace(operation)) throw new InvalidDataException("Bridge request is missing operation.");

            return new ParsedBridgeRequest(
                new BridgeRequestEnvelope
                {
                    ProtocolVersion = GetString(root, "protocolVersion") ?? BridgeProtocol.Version,
                    RequestId = requestId,
                    SessionId = GetString(root, "sessionId") ?? string.Empty,
                    AuthToken = GetString(root, "authToken"),
                    Operation = operation,
                    OperationKind = GetString(root, "operationKind") ?? "read",
                    TimeoutMs = GetInt(root, "timeoutMs") ?? 30000,
                    DocumentFingerprint = GetString(root, "documentFingerprint"),
                    ExpectedGeneration = GetLong(root, "expectedGeneration"),
                    Payload = GetDictionary(root, "payload") ?? new Dictionary<string, object>()
                },
                GetString(root, "authToken"));
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

            return JsonWireCodec.Serialize(body);
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

        private sealed class ParsedBridgeRequest
        {
            public ParsedBridgeRequest(BridgeRequestEnvelope request, string authToken)
            {
                Request = request;
                AuthToken = authToken;
            }

            public BridgeRequestEnvelope Request { get; }
            public string AuthToken { get; }
        }
    }
}
