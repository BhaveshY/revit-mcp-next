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
        private const int ControlWorkerId = AcceptWorkerCount;
        private const int HandshakeTimeoutMs = 10000;
        private const int ResponseWriteTimeoutMs = 5000;
        private const int InitialAcceptRetryDelayMs = 100;
        private const int MaxAcceptRetryDelayMs = 2000;
        private const int ShutdownWaitMs = 5000;

        private readonly string _pipeName;
        private readonly RevitRequestQueue _requestQueue;
        private readonly RequestOutcomeLedger _requestOutcomes;
        private readonly PipeAuthOptions _authOptions;
        private readonly PipeSecurity _pipeSecurity;
        private readonly Func<NamedPipeServerStream> _serverFactory;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _lifecycleGate = new object();
        private readonly NamedPipeServerStream[] _workerStreams = new NamedPipeServerStream[AcceptWorkerCount];
        private NamedPipeServerStream _controlStream;
        private Task[] _acceptWorkers = Array.Empty<Task>();
        private Task _controlWorker;
        private int _started;
        private int _disposed;
        private int _primaryWaitingListenerCount;
        private int _controlWaitingListenerCount;
        private int _primaryActiveConnectionCount;
        private int _controlActiveConnectionCount;
        private long _acceptedConnectionCount;
        private long _completedConnectionCount;
        private long _clientFaultCount;
        private long _listenerFaultCount;
        private long _lastAcceptedUtcTicks;
        private long _lastFaultUtcTicks;

        public NamedPipeHost(string pipeName, RevitRequestQueue requestQueue, PipeAuthOptions authOptions = null)
            : this(pipeName, requestQueue, authOptions, null, null)
        {
        }

        internal NamedPipeHost(
            string pipeName,
            RevitRequestQueue requestQueue,
            PipeAuthOptions authOptions,
            Func<NamedPipeServerStream> serverFactory,
            RequestOutcomeLedger requestOutcomes = null)
        {
            _pipeName = pipeName;
            _requestQueue = requestQueue;
            _requestOutcomes = requestOutcomes ?? new RequestOutcomeLedger();
            _requestQueue.RequestStarted += HandleRequestStarted;
            _authOptions = authOptions ?? PipeAuthOptions.FromEnvironment();
            _pipeSecurity = PipeSecurityFactory.CreateCurrentUserOnly();
            _serverFactory = serverFactory;
        }

        internal string ControlPipeName => _pipeName + "-control";

        public void Start()
        {
            NamedPipeServerStream[] initialStreams = new NamedPipeServerStream[AcceptWorkerCount];
            NamedPipeServerStream initialControlStream = null;
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
                        initialStreams[workerId] = CreateServerStream(controlOnly: false);
                        _workerStreams[workerId] = initialStreams[workerId];
                    }
                    initialControlStream = CreateServerStream(controlOnly: true);
                    _controlStream = initialControlStream;
                }
                catch
                {
                    for (int workerId = 0; workerId < initialStreams.Length; workerId++)
                    {
                        DisposeStream(initialStreams[workerId]);
                        _workerStreams[workerId] = null;
                    }
                    DisposeStream(initialControlStream);
                    _controlStream = null;
                    throw;
                }

                _started = 1;
                _acceptWorkers = initialStreams
                    .Select((stream, workerId) => Task.Run(
                        () => AcceptWorkerAsync(workerId, stream, controlOnly: false, _shutdown.Token)))
                    .ToArray();
                _controlWorker = Task.Run(
                    () => AcceptWorkerAsync(ControlWorkerId, initialControlStream, controlOnly: true, _shutdown.Token));
            }

            DiagnosticsLogger.Info(
                "Named pipe host started with " + AcceptWorkerCount.ToString(CultureInfo.InvariantCulture) +
                " bounded accept workers and one reserved control worker on " + ControlPipeName + ". " + DescribeHealth());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _requestQueue.RequestStarted -= HandleRequestStarted;
            _shutdown.Cancel();
            Task[] workers;
            NamedPipeServerStream[] streams;
            lock (_lifecycleGate)
            {
                workers = _acceptWorkers.ToArray();
                if (_controlWorker != null) workers = workers.Concat(new[] { _controlWorker }).ToArray();
                streams = _workerStreams.ToArray();
                for (int workerId = 0; workerId < _workerStreams.Length; workerId++)
                {
                    _workerStreams[workerId] = null;
                }
                if (_controlStream != null) streams = streams.Concat(new[] { _controlStream }).ToArray();
                _controlStream = null;
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
            bool controlOnly,
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
                        server = CreateServerStream(controlOnly);
                        if (!RegisterWorkerStream(workerId, server, controlOnly, cancellationToken))
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
                    if (controlOnly)
                    {
                        Interlocked.Increment(ref _controlWaitingListenerCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref _primaryWaitingListenerCount);
                    }
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

                    ClearWorkerStream(workerId, server, controlOnly);
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
                    if (controlOnly)
                    {
                        Interlocked.Decrement(ref _controlWaitingListenerCount);
                    }
                    else
                    {
                        Interlocked.Decrement(ref _primaryWaitingListenerCount);
                    }
                }

                try
                {
                    await HandleClientSafelyAsync(server, workerId, controlOnly, cancellationToken).ConfigureAwait(false);
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
                    ClearWorkerStream(workerId, server, controlOnly);
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
            bool controlOnly,
            CancellationToken cancellationToken)
        {
            if (controlOnly)
            {
                Interlocked.Increment(ref _controlActiveConnectionCount);
            }
            else
            {
                Interlocked.Increment(ref _primaryActiveConnectionCount);
            }
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
                    BridgeProtocolStatus protocolStatus = BridgeProtocolGuard.Classify(request.BridgeProtocolVersion);
                    if (!_authOptions.IsAuthorized(parsedRequest.AuthToken))
                    {
                        response = Failure(
                            request,
                            "AUTHENTICATION_FAILED",
                            "Bridge request authentication failed.",
                            "Set REVIT_MCP_NEXT_AUTH_TOKEN for the broker to the same value configured for the Revit add-in.");
                    }
                    else if (protocolStatus == BridgeProtocolStatus.Missing)
                    {
                        response = Failure(
                            request,
                            "PROTOCOL_VERSION_REQUIRED",
                            "Bridge request is missing protocolVersion.",
                            "Rebuild and restart the MCP client so every request includes the current protocol version.");
                    }
                    else if (protocolStatus == BridgeProtocolStatus.Mismatch)
                    {
                        response = Failure(
                            request,
                            "PROTOCOL_VERSION_MISMATCH",
                            "Broker bridge protocol " + request.BridgeProtocolVersion + " does not match add-in bridge protocol " + BridgeProtocol.Version + ".",
                            "Rebuild and reinstall Revit MCP Next so the broker and add-in use the same version.");
                    }
                    else if (IsDirectOperation(request.Operation))
                    {
                        response = HandleDirectOperation(request);
                    }
                    else if (controlOnly)
                    {
                        response = Failure(
                            request,
                            "CONTROL_OPERATION_REQUIRED",
                            "The reserved control pipe only accepts bridge_health, cancel_request, and get_request_result.",
                            "Send model reads and writes to the primary Revit MCP pipe.");
                    }
                    else
                    {
                        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            requestTimeout.CancelAfter(ComputeServerQueueTimeoutMs(request.TimeoutMs));
                            response = await ExecuteQueuedRequestAsync(request, requestTimeout.Token).ConfigureAwait(false);
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
                if (controlOnly)
                {
                    Interlocked.Decrement(ref _controlActiveConnectionCount);
                }
                else
                {
                    Interlocked.Decrement(ref _primaryActiveConnectionCount);
                }
            }
        }

        private bool RegisterWorkerStream(
            int workerId,
            NamedPipeServerStream stream,
            bool controlOnly,
            CancellationToken cancellationToken)
        {
            lock (_lifecycleGate)
            {
                if (_disposed != 0 || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (controlOnly) _controlStream = stream;
                else _workerStreams[workerId] = stream;
                return true;
            }
        }

        private async Task<BridgeResponseEnvelope> ExecuteQueuedRequestAsync(
            BridgeRequestEnvelope request,
            CancellationToken cancellationToken)
        {
            if (!IsWriteRequest(request))
            {
                return await _requestQueue.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            }

            RequestOutcomeLease lease = _requestOutcomes.Acquire(request, ComputeRequestFingerprint(request));
            if (lease.IsError)
            {
                return Failure(request, lease.ErrorCode, lease.ErrorMessage);
            }

            if (!lease.IsOwner)
            {
                RequestOutcome replay = await AwaitOutcomeAsync(lease.Completion, cancellationToken).ConfigureAwait(false);
                return replay?.Response ?? Failure(
                    request,
                    "REQUEST_RESULT_PENDING",
                    "The original write is still pending and this replay request reached its timeout.",
                    "Call get_request_result with the same requestId on the reserved control pipe.");
            }

            BridgeResponseEnvelope response;
            try
            {
                response = await _requestQueue.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = Failure(
                    request,
                    "REQUEST_EXECUTION_FAILED",
                    ex.Message,
                    "Inspect bridge_health and retry with the same requestId.");
            }

            _requestOutcomes.Complete(
                request.SessionId,
                request.RequestId,
                response,
                TerminalState(response));
            return response;
        }

        private void HandleRequestStarted(BridgeRequestEnvelope request)
        {
            if (IsWriteRequest(request))
            {
                _requestOutcomes.MarkRunning(request.SessionId, request.RequestId);
            }
        }

        private static string ComputeRequestFingerprint(BridgeRequestEnvelope request)
        {
            return CanonicalJson.Serialize(new Dictionary<string, object>
            {
                ["operation"] = request.Operation,
                ["operationKind"] = request.OperationKind,
                ["instanceId"] = request.InstanceId,
                ["documentFingerprint"] = request.DocumentFingerprint,
                ["expectedGeneration"] = request.ExpectedGeneration,
                ["payload"] = request.Payload ?? new Dictionary<string, object>()
            });
        }

        private BridgeResponseEnvelope HandleDirectOperation(BridgeRequestEnvelope request)
        {
            switch (request.Operation)
            {
                case "bridge_health":
                    return Success(request, BuildBridgeHealth());
                case "cancel_request":
                    return HandleDirectCancel(request);
                case "get_request_result":
                    return HandleGetRequestResult(request);
                default:
                    return Failure(request, "UNSUPPORTED_CONTROL_OPERATION", "Unsupported control operation: " + request.Operation);
            }
        }

        private BridgeResponseEnvelope HandleDirectCancel(BridgeRequestEnvelope request)
        {
            string targetRequestId = GetString(request.Payload, "requestId");
            string reason = GetString(request.Payload, "reason");
            bool cancelled = _requestQueue.TryCancelQueued(targetRequestId, reason, request.SessionId ?? string.Empty);
            var data = new Dictionary<string, object>
            {
                ["cancelled"] = cancelled,
                ["message"] = cancelled
                    ? "Queued request cancelled before Revit processed it."
                    : "No queued cancellable request matched. In-flight Revit API work cannot be interrupted safely."
            };
            if (!string.IsNullOrWhiteSpace(targetRequestId)) data["requestId"] = targetRequestId;
            if (!string.IsNullOrWhiteSpace(reason)) data["reason"] = reason;
            return Success(request, data);
        }

        private BridgeResponseEnvelope HandleGetRequestResult(BridgeRequestEnvelope request)
        {
            string targetRequestId = GetString(request.Payload, "requestId");
            if (string.IsNullOrWhiteSpace(targetRequestId))
            {
                return Failure(request, "REQUEST_ID_REQUIRED", "get_request_result requires payload.requestId.");
            }

            if (!_requestOutcomes.TryGet(request.SessionId, targetRequestId, out RequestOutcome outcome))
            {
                return Success(request, new Dictionary<string, object>
                {
                    ["found"] = false,
                    ["state"] = "failed"
                });
            }

            var data = new Dictionary<string, object>
            {
                ["found"] = true,
                ["state"] = StateName(outcome.State),
                ["requestId"] = outcome.RequestId,
                ["operation"] = outcome.Operation,
                ["acceptedAtUtc"] = outcome.AcceptedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            };
            if (outcome.CompletedAtUtc.HasValue)
            {
                data["completedAtUtc"] = outcome.CompletedAtUtc.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }
            if (outcome.Response != null) data["response"] = ToWireResponse(outcome.Response);
            return Success(request, data);
        }

        private Dictionary<string, object> BuildBridgeHealth()
        {
            int primaryWaitingListeners = Volatile.Read(ref _primaryWaitingListenerCount);
            int controlWaitingListeners = Volatile.Read(ref _controlWaitingListenerCount);
            int primaryActiveConnections = Volatile.Read(ref _primaryActiveConnectionCount);
            int controlActiveConnections = Volatile.Read(ref _controlActiveConnectionCount);
            var data = new Dictionary<string, object>
            {
                ["healthy"] = _disposed == 0 && _started != 0 && primaryWaitingListeners > 0,
                ["pipeName"] = _pipeName,
                ["controlPipeName"] = ControlPipeName,
                ["waitingListeners"] = primaryWaitingListeners + controlWaitingListeners,
                ["activeConnections"] = primaryActiveConnections + controlActiveConnections,
                ["primaryWaitingListeners"] = primaryWaitingListeners,
                ["controlWaitingListeners"] = controlWaitingListeners,
                ["primaryActiveConnections"] = primaryActiveConnections,
                ["controlActiveConnections"] = controlActiveConnections,
                ["acceptedConnections"] = Interlocked.Read(ref _acceptedConnectionCount),
                ["completedConnections"] = Interlocked.Read(ref _completedConnectionCount),
                ["clientFaults"] = Interlocked.Read(ref _clientFaultCount),
                ["listenerFaults"] = Interlocked.Read(ref _listenerFaultCount),
                ["queue"] = _requestQueue.GetDiagnosticsSnapshot(),
                ["requestOutcomes"] = _requestOutcomes.GetDiagnosticsSnapshot()
            };

            long lastAccepted = Interlocked.Read(ref _lastAcceptedUtcTicks);
            long lastFault = Interlocked.Read(ref _lastFaultUtcTicks);
            if (lastAccepted > 0) data["lastAcceptedAtUtc"] = FormatTimestamp(lastAccepted);
            if (lastFault > 0) data["lastFaultAtUtc"] = FormatTimestamp(lastFault);
            return data;
        }

        private static async Task<RequestOutcome> AwaitOutcomeAsync(
            Task<RequestOutcome> completion,
            CancellationToken cancellationToken)
        {
            var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
            {
                Task completed = await Task.WhenAny(completion, cancellation.Task).ConfigureAwait(false);
                return ReferenceEquals(completed, completion)
                    ? await completion.ConfigureAwait(false)
                    : null;
            }
        }

        private static bool IsDirectOperation(string operation)
        {
            return string.Equals(operation, "bridge_health", StringComparison.Ordinal) ||
                   string.Equals(operation, "cancel_request", StringComparison.Ordinal) ||
                   string.Equals(operation, "get_request_result", StringComparison.Ordinal);
        }

        private static bool IsWriteRequest(BridgeRequestEnvelope request)
        {
            return string.Equals(request.OperationKind, "write", StringComparison.Ordinal) ||
                   string.Equals(request.OperationKind, "destructive", StringComparison.Ordinal);
        }

        private static RequestOutcomeState TerminalState(BridgeResponseEnvelope response)
        {
            if (response?.Ok == true) return RequestOutcomeState.Committed;
            return string.Equals(response?.Error?.Code, "REVIT_COMMAND_FAILED", StringComparison.Ordinal)
                ? RequestOutcomeState.RolledBack
                : RequestOutcomeState.Failed;
        }

        private static string StateName(RequestOutcomeState state)
        {
            switch (state)
            {
                case RequestOutcomeState.Accepted: return "accepted";
                case RequestOutcomeState.Running: return "running";
                case RequestOutcomeState.Committed: return "committed";
                case RequestOutcomeState.RolledBack: return "rolledBack";
                default: return "failed";
            }
        }

        private void ClearWorkerStream(int workerId, NamedPipeServerStream stream, bool controlOnly)
        {
            lock (_lifecycleGate)
            {
                if (controlOnly)
                {
                    if (ReferenceEquals(_controlStream, stream)) _controlStream = null;
                }
                else if (ReferenceEquals(_workerStreams[workerId], stream))
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
                " primaryWaitingListeners=" + Volatile.Read(ref _primaryWaitingListenerCount).ToString(CultureInfo.InvariantCulture) +
                " controlWaitingListeners=" + Volatile.Read(ref _controlWaitingListenerCount).ToString(CultureInfo.InvariantCulture) +
                " primaryActiveConnections=" + Volatile.Read(ref _primaryActiveConnectionCount).ToString(CultureInfo.InvariantCulture) +
                " controlActiveConnections=" + Volatile.Read(ref _controlActiveConnectionCount).ToString(CultureInfo.InvariantCulture) +
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

        private NamedPipeServerStream CreateServerStream(bool controlOnly)
        {
            if (!controlOnly && _serverFactory != null)
            {
                return _serverFactory();
            }

            string pipeName = controlOnly ? ControlPipeName : _pipeName;
            int maxInstances = controlOnly ? 1 : AcceptWorkerCount;

#if NETFRAMEWORK
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                DefaultPipeBufferSize,
                DefaultPipeBufferSize,
                _pipeSecurity);
#else
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                DefaultPipeBufferSize,
                DefaultPipeBufferSize,
                _pipeSecurity);
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
                    BridgeProtocolVersion = GetString(root, "protocolVersion"),
                    RequestId = requestId,
                    SessionId = GetString(root, "sessionId") ?? string.Empty,
                    AuthToken = GetString(root, "authToken"),
                    Operation = operation,
                    OperationKind = GetString(root, "operationKind") ?? "read",
                    TimeoutMs = GetInt(root, "timeoutMs") ?? 30000,
                    InstanceId = GetString(root, "instanceId"),
                    DocumentFingerprint = GetString(root, "documentFingerprint"),
                    ExpectedGeneration = GetLong(root, "expectedGeneration"),
                    Payload = GetDictionary(root, "payload") ?? new Dictionary<string, object>()
                },
                GetString(root, "authToken"));
        }

        private static string SerializeResponse(BridgeResponseEnvelope response)
        {
            return JsonWireCodec.Serialize(ToWireResponse(response));
        }

        private static Dictionary<string, object> ToWireResponse(BridgeResponseEnvelope response)
        {
            var body = new Dictionary<string, object>
            {
                ["ok"] = response.Ok,
                ["requestId"] = response.RequestId,
                ["warnings"] = (response.Warnings ?? new List<BridgeWarning>()).Select(ToWireWarning).ToArray(),
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

            return body;
        }

        private static BridgeResponseEnvelope Success(
            BridgeRequestEnvelope request,
            object data)
        {
            return new BridgeResponseEnvelope
            {
                Ok = true,
                RequestId = request.RequestId,
                Data = data ?? new Dictionary<string, object>(),
                Metrics = new BridgeMetrics { ElapsedMs = 0 }
            };
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
            if (metrics?.QueueWaitMs != null) body["queueWaitMs"] = metrics.QueueWaitMs.Value;
            if (metrics?.RevitExecutionMs != null) body["revitExecutionMs"] = metrics.RevitExecutionMs.Value;
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
