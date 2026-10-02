using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    /// <summary>
    /// Protocol v3 pipe host (SPEC §4.6, §8.3; D2 §5.6): 16 primary and 4 control pipe instances, one request per
    /// connection, 5 s handshake. hello is answered without auth; every other op is protocol-range and auth checked
    /// first. Control ops are served on the pipe thread; everything else is queued for the Revit UI thread (asJob
    /// requests become jobs answered at once). Write/lifecycle/code outcomes go to the ledger. The pipe ACL admits the
    /// current user only.
    /// </summary>
    internal sealed class NamedPipeHost : IDisposable
    {
        private const int InitialAcceptRetryDelayMs = 100;
        private const int MaxAcceptRetryDelayMs = 2000;
        private const int ShutdownWaitMs = 5000;
        /// <summary>A queued item is auto-cancelled this long before the request's time budget ends (D2 §12.2).</summary>
        private const int QueueCancelMarginMs = 750;
        /// <summary>A running item keeps the connection open at most this long past the request's time budget.</summary>
        private const int RunningGraceMs = 2000;

        private readonly string _pipeName;
        private readonly string _controlPipeName;
        private readonly RevitRequestQueue _queue;
        private readonly RequestOutcomeLedger _ledger;
        private readonly AuthTokenStore _auth;
        private readonly JobRunner _jobs;
        private readonly ControlOperations _control;
        private readonly PipeSecurity _pipeSecurity;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _lifecycleGate = new object();
        private readonly List<Task> _workers = new List<Task>();
        private readonly List<NamedPipeServerStream> _streams = new List<NamedPipeServerStream>();
        private int _started;
        private int _disposed;
        private int _primaryWaiting;
        private int _primaryActive;
        private int _controlWaiting;
        private int _controlActive;
        private long _accepted;
        private long _completed;
        private long _clientFaults;
        private long _listenerFaults;
        /// <summary>Requests refused by admission control (REVIT_QUEUE_FULL; P-REL-ADDIN adds admission).</summary>
        private long _admissionRejections = 0;

        public NamedPipeHost(
            string pipeName,
            string controlPipeName,
            RevitRequestQueue queue,
            RequestOutcomeLedger ledger,
            AuthTokenStore auth,
            JobRunner jobs)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _controlPipeName = controlPipeName ?? throw new ArgumentNullException(nameof(controlPipeName));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _ledger = ledger;
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _jobs = jobs;
            _pipeSecurity = PipeSecurityFactory.CreateCurrentUserOnly();
            _control = new ControlOperations(GetListenerHealth, () => Interlocked.Read(ref _admissionRejections));
            _queue.RequestStarted += item => { if (RequestOutcomeLedger.IsRecorded(item.Request)) _ledger?.MarkRunning(item.RequestId); };
        }

        public string PipeName => _pipeName;
        public string ControlPipeName => _controlPipeName;

        public void Start()
        {
            lock (_lifecycleGate)
            {
                if (_disposed != 0) throw new ObjectDisposedException(nameof(NamedPipeHost));
                if (_started != 0) throw new InvalidOperationException("The named pipe host has already been started.");
                _started = 1;
                for (int worker = 0; worker < BridgeProtocol.PrimaryPipeInstances; worker++)
                {
                    int id = worker;
                    _workers.Add(Task.Run(() => AcceptLoopAsync(id, control: false, _shutdown.Token)));
                }
                for (int worker = 0; worker < BridgeProtocol.ControlPipeInstances; worker++)
                {
                    int id = worker;
                    _workers.Add(Task.Run(() => AcceptLoopAsync(id, control: true, _shutdown.Token)));
                }
            }
            DiagnosticsLogger.Info("pipe", "Pipe host started: " + _pipeName + " (" + BridgeProtocol.PrimaryPipeInstances.ToString(CultureInfo.InvariantCulture) +
                " instances) and " + _controlPipeName + " (" + BridgeProtocol.ControlPipeInstances.ToString(CultureInfo.InvariantCulture) + " instances).");
        }

        public ListenerHealth GetListenerHealth()
        {
            return new ListenerHealth
            {
                PrimaryWaiting = Volatile.Read(ref _primaryWaiting),
                PrimaryActive = Volatile.Read(ref _primaryActive),
                ControlWaiting = Volatile.Read(ref _controlWaiting),
                ControlActive = Volatile.Read(ref _controlActive)
            };
        }

        public Dictionary<string, object> GetCounters()
        {
            return new Dictionary<string, object>
            {
                ["accepted"] = Interlocked.Read(ref _accepted),
                ["completed"] = Interlocked.Read(ref _completed),
                ["clientFaults"] = Interlocked.Read(ref _clientFaults),
                ["listenerFaults"] = Interlocked.Read(ref _listenerFaults)
            };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _shutdown.Cancel();
            NamedPipeServerStream[] streams;
            Task[] workers;
            lock (_lifecycleGate)
            {
                streams = _streams.ToArray();
                _streams.Clear();
                workers = _workers.ToArray();
            }
            foreach (NamedPipeServerStream stream in streams) DisposeStream(stream);
            try
            {
                if (workers.Length > 0 && !Task.WaitAll(workers, ShutdownWaitMs))
                {
                    DiagnosticsLogger.Warn("pipe", "Timed out waiting for pipe workers to stop.");
                }
            }
            catch (AggregateException ex)
            {
                Exception unexpected = ex.Flatten().InnerExceptions.FirstOrDefault(e => !(e is OperationCanceledException) && !(e is ObjectDisposedException));
                if (unexpected != null) DiagnosticsLogger.Error("pipe", "Pipe workers faulted during shutdown.", unexpected);
            }
            DiagnosticsLogger.Info("pipe", "Pipe host stopped.");
            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync(int workerId, bool control, CancellationToken cancellationToken)
        {
            int consecutiveFaults = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServerStream(control);
                    if (!Track(server))
                    {
                        DisposeStream(server);
                        return;
                    }
                    if (control) Interlocked.Increment(ref _controlWaiting); else Interlocked.Increment(ref _primaryWaiting);
                    try
                    {
                        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (control) Interlocked.Decrement(ref _controlWaiting); else Interlocked.Decrement(ref _primaryWaiting);
                    }
                    consecutiveFaults = 0;
                    Interlocked.Increment(ref _accepted);
                    await HandleClientAsync(server, workerId, control, cancellationToken).ConfigureAwait(false);
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
                    if (cancellationToken.IsCancellationRequested) return;
                    Interlocked.Increment(ref _listenerFaults);
                    DiagnosticsLogger.Error("pipe", (control ? "Control" : "Primary") + " pipe worker " + workerId.ToString(CultureInfo.InvariantCulture) + " faulted; retrying.", ex);
                    consecutiveFaults++;
                    int delay = Math.Min(MaxAcceptRetryDelayMs, InitialAcceptRetryDelayMs * (1 << Math.Min(4, consecutiveFaults - 1)));
                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                finally
                {
                    Untrack(server);
                    DisposeStream(server);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, int workerId, bool control, CancellationToken cancellationToken)
        {
            if (control) Interlocked.Increment(ref _controlActive); else Interlocked.Increment(ref _primaryActive);
            string requestId = null;
            string op = null;
            try
            {
                string requestJson = await ReadWithDeadlineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (requestJson == null)
                {
                    Interlocked.Increment(ref _clientFaults);
                    DiagnosticsLogger.Info("pipe", "client_gone: no request frame within " + BridgeProtocol.HandshakeTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms.");
                    return;
                }

                BridgeResponse response;
                BridgeRequest request = null;
                try
                {
                    request = Parse(requestJson);
                    requestId = request.RequestId;
                    op = request.Op;
                    response = await ProcessAsync(request, control, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (InvalidDataException ex)
                {
                    response = BridgeResponse.Failure(requestId, ErrorCodes.InvalidArgs, "Malformed bridge request: " + ex.Message,
                        new Dictionary<string, object> { ["param"] = "request", ["reason"] = ex.Message });
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _clientFaults);
                    DiagnosticsLogger.Error("pipe", "Processing request " + (requestId ?? "(unparsed)") + " failed.", ex);
                    response = BridgeResponse.Failure(requestId, ErrorCodes.InternalError, ex.Message,
                        new Dictionary<string, object> { ["requestId"] = requestId, ["logPath"] = DiagnosticsLogger.CurrentLogFile });
                }

                response.RequestId = string.IsNullOrEmpty(response.RequestId) ? (requestId ?? string.Empty) : response.RequestId;
                string json = SerializeResponse(response);
                bool written = await WriteWithDeadlineAsync(stream, json, cancellationToken).ConfigureAwait(false);
                if (written) Interlocked.Increment(ref _completed);
                LogRequest(request, response, control, written);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown.
            }
            catch (IOException ex)
            {
                Interlocked.Increment(ref _clientFaults);
                DiagnosticsLogger.Info("pipe", "client_gone: requestId=" + (requestId ?? "(unparsed)") + " op=" + (op ?? "(unparsed)") + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _clientFaults);
                DiagnosticsLogger.Error("pipe", "Pipe client on worker " + workerId.ToString(CultureInfo.InvariantCulture) + " failed. requestId=" + (requestId ?? "(unparsed)"), ex);
            }
            finally
            {
                if (control) Interlocked.Decrement(ref _controlActive); else Interlocked.Decrement(ref _primaryActive);
            }
        }

        /// <summary>Order: hello (no auth) → protocol range → auth → control op | queue.</summary>
        private async Task<BridgeResponse> ProcessAsync(BridgeRequest request, bool control, CancellationToken cancellationToken)
        {
            if (request.Op == ControlOps.Hello) return _control.Handle(request);

            BridgeResponse protocolError = BridgeProtocolGuard.Check(request, McpRuntime.Instance?.AddinVersion);
            if (protocolError != null) return protocolError;

            string authError = _auth.Verify(request.Auth?.Token, request.Auth?.Fp, request.Auth?.File, out Dictionary<string, object> authDetails);
            if (authError != null)
            {
                string message = authError == ErrorCodes.AuthNotConfigured
                    ? "Revit could not set up its auth token file " + _auth.AuthFile + "."
                    : "The broker's auth token (fp " + (authDetails?["brokerFp"] ?? "?") + ") does not match Revit's (fp " + _auth.Fingerprint + ", " + _auth.AuthFile + ").";
                return BridgeResponse.Failure(request.RequestId, authError, message, authDetails);
            }

            if (ControlOps.IsControlOp(request.Op)) return _control.Handle(request);
            if (control)
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.UnknownOp,
                    "The control pipe serves control ops only; send '" + request.Op + "' to the primary pipe.",
                    new Dictionary<string, object> { ["ops"] = ControlOps.All.ToList(), ["pipe"] = _pipeName });
            }
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return BridgeResponse.Failure(null, ErrorCodes.InvalidArgs, "The request has no requestId.",
                    new Dictionary<string, object> { ["param"] = "requestId", ["reason"] = "required" });
            }
            if (string.IsNullOrWhiteSpace(request.Op))
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.InvalidArgs, "The request has no op.",
                    new Dictionary<string, object> { ["param"] = "op", ["reason"] = "required" });
            }

            return request.AsJob
                ? StartJob(request)
                : await ExecuteQueuedAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private BridgeResponse StartJob(BridgeRequest request)
        {
            if (_jobs == null)
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.UnsupportedOp, "Jobs are not available in this add-in build.");
            }
            LedgerEntry ledgerEntry = null;
            if (_ledger != null && RequestOutcomeLedger.IsRecorded(request))
            {
                LedgerLease lease = _ledger.Acquire(request, request.Doc?.Key);
                if (lease.IsError) return BridgeResponse.Failure(request.RequestId, lease.ErrorCode, lease.ErrorMessage);
                if (!lease.IsOwner)
                {
                    // Same job_start sent twice: report the existing job when it is still known.
                    JobRecord existing = _jobs.FindByRequest(request.RequestId);
                    if (existing != null)
                    {
                        BridgeResponse again = BridgeResponse.Success(request.RequestId, null, "job " + existing.JobId + " already started");
                        again.Job = existing.ToJobInfo();
                        return again;
                    }
                }
                ledgerEntry = lease.Entry;
            }

            JobRecord job = _jobs.Submit(request, step => Dispatcher.RunRequestAsJobStep(step, request));
            if (ledgerEntry != null)
            {
                job.Completion.Task.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion && task.Result != null) _ledger.Complete(request.RequestId, task.Result);
                }, TaskScheduler.Default);
            }
            BridgeResponse response = BridgeResponse.Success(request.RequestId, null, "queued as job");
            response.Job = job.ToJobInfo();
            return response;
        }

        private async Task<BridgeResponse> ExecuteQueuedAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            bool recorded = _ledger != null && RequestOutcomeLedger.IsRecorded(request);
            if (recorded)
            {
                LedgerLease lease = _ledger.Acquire(request, request.Doc?.Key);
                if (lease.IsError) return BridgeResponse.Failure(request.RequestId, lease.ErrorCode, lease.ErrorMessage);
                if (!lease.IsOwner)
                {
                    // A replay of a write we already accepted: return its outcome (never execute twice).
                    Task<LedgerEntry> completion = lease.Entry.Completion.Task;
                    Task finished = await Task.WhenAny(completion, Task.Delay(Math.Max(1, request.TimeoutMs), cancellationToken)).ConfigureAwait(false);
                    if (ReferenceEquals(finished, completion) && completion.Result?.Response != null) return completion.Result.Response;
                    return BridgeResponse.Failure(request.RequestId, ErrorCodes.WriteStillRunning,
                        "The write " + request.Op + " with this requestId is still running.",
                        new Dictionary<string, object> { ["requestId"] = request.RequestId, ["ref"] = request.WriteTag });
                }
            }

            using (var queueTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                queueTimeout.CancelAfter(Math.Max(1, request.TimeoutMs - QueueCancelMarginMs));
                QueuedRevitWorkItem item = _queue.Enqueue(request, queueTimeout.Token);
                Task<BridgeResponse> completion = item.Completion.Task;
                int maxWaitMs = Math.Max(1, request.TimeoutMs) + RunningGraceMs;
                Task finished = await Task.WhenAny(completion, Task.Delay(maxWaitMs, cancellationToken)).ConfigureAwait(false);

                BridgeResponse response;
                if (ReferenceEquals(finished, completion))
                {
                    response = completion.Result;
                    if (!response.Ok && response.Code == ErrorCodes.RequestCancelled && queueTimeout.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        response = NotPickedUp(request);
                    }
                }
                else
                {
                    // Still running past the budget: answer now; the ledger records the outcome when it finishes.
                    bool isWrite = recorded;
                    response = BridgeResponse.Failure(request.RequestId,
                        isWrite ? ErrorCodes.WriteStillRunning : ErrorCodes.ReadStillRunning,
                        request.Op + " is still running in Revit after " + maxWaitMs.ToString(CultureInfo.InvariantCulture) + " ms.",
                        new Dictionary<string, object> { ["requestId"] = request.RequestId, ["ref"] = request.WriteTag });
                    if (recorded)
                    {
                        _ = completion.ContinueWith(task =>
                        {
                            if (task.Status == TaskStatus.RanToCompletion) _ledger.Complete(request.RequestId, task.Result);
                        }, TaskScheduler.Default);
                    }
                    return response;
                }

                if (recorded) _ledger.Complete(request.RequestId, response);
                return response;
            }
        }

        private BridgeResponse NotPickedUp(BridgeRequest request)
        {
            ExecutingInfo executing = _queue.Executing;
            var details = new Dictionary<string, object>
            {
                ["reason"] = "not_picked_up",
                ["pending"] = _queue.PendingCount,
                ["executing"] = executing?.ToHealthWire(),
                ["native"] = McpRuntime.Registry?.Native?.ToWire()
            };
            string what = executing != null
                ? "Revit is executing " + executing.Op + " (" + (executing.ElapsedMs / 1000).ToString(CultureInfo.InvariantCulture) + " s)"
                : "Revit did not pick the request up (it may show a dialog or be in a command)";
            return BridgeResponse.Failure(request.RequestId, ErrorCodes.RevitBusy,
                what + "; " + request.Op + " was cancelled before it ran.", details);
        }

        private static BridgeRequest Parse(string json)
        {
            object parsed = JsonWireCodec.DeserializeObject(json);
            if (!(parsed is IDictionary<string, object> root))
            {
                throw new InvalidDataException("The request must be a JSON object.");
            }
            return BridgeRequest.FromWire(root);
        }

        /// <summary>Serializes within the 4 MiB frame: non-finite numbers → null + NON_FINITE_NUMBER; failures get own codes.</summary>
        internal static string SerializeResponse(BridgeResponse response)
        {
            string failure;
            string code = ErrorCodes.ResponseSerializationFailed;
            try
            {
                string json = JsonWireCodec.Serialize(response.ToWire(), out int nonFinite);
                if (nonFinite > 0)
                {
                    OpResult.AddWarning(response.Warnings, WarningCodes.NonFiniteNumber,
                        nonFinite.ToString(CultureInfo.InvariantCulture) + " non-finite number(s) were returned as null.", null, nonFinite);
                    json = JsonWireCodec.Serialize(response.ToWire());
                }
                int bytes = Encoding.UTF8.GetByteCount(json);
                if (bytes <= BridgeProtocol.MaxFrameBytes) return json;
                code = ErrorCodes.ResponseTooLarge;
                failure = "The response is " + bytes.ToString(CultureInfo.InvariantCulture) + " bytes, above the " +
                          BridgeProtocol.MaxFrameBytes.ToString(CultureInfo.InvariantCulture) + " byte frame limit.";
            }
            catch (Exception ex)
            {
                failure = "The response could not be serialized: " + ex.Message;
            }

            DiagnosticsLogger.Warn("pipe", code + " for requestId=" + response.RequestId + ": " + failure);
            BridgeResponse replacement = BridgeResponse.Failure(response.RequestId, code, failure,
                new Dictionary<string, object> { ["op"] = response.Summary });
            replacement.Doc = response.Doc;
            replacement.Metrics = response.Metrics;
            return JsonWireCodec.Serialize(replacement.ToWire());
        }

        private async Task<string> ReadWithDeadlineAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
        {
            Task<string> read = FramedPipeTransport.ReadFrameAsync(stream, cancellationToken);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task timeout = Task.Delay(BridgeProtocol.HandshakeTimeoutMs, deadline.Token);
                Task first = await Task.WhenAny(read, timeout).ConfigureAwait(false);
                if (ReferenceEquals(first, read))
                {
                    deadline.Cancel();
                    return await read.ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            DisposeStream(stream);
            try
            {
                await read.ConfigureAwait(false);
            }
            catch
            {
                // Disposing the stream is how net48 reliably interrupts a pending pipe read.
            }
            return null;
        }

        private static async Task<bool> WriteWithDeadlineAsync(NamedPipeServerStream stream, string json, CancellationToken cancellationToken)
        {
            Task write = WriteAndDrainAsync(stream, json, cancellationToken);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task timeout = Task.Delay(BridgeProtocol.ResponseWriteTimeoutMs, deadline.Token);
                Task first = await Task.WhenAny(write, timeout).ConfigureAwait(false);
                if (ReferenceEquals(first, write))
                {
                    deadline.Cancel();
                    await write.ConfigureAwait(false);
                    return true;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            DisposeStream(stream);
            try
            {
                await write.ConfigureAwait(false);
            }
            catch
            {
                // Disposing the stream interrupts the pending write.
            }
            DiagnosticsLogger.Info("pipe", "client_gone: the client did not read the response within " +
                BridgeProtocol.ResponseWriteTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms.");
            return false;
        }

        private static async Task WriteAndDrainAsync(NamedPipeServerStream stream, string json, CancellationToken cancellationToken)
        {
            await FramedPipeTransport.WriteFrameAsync(stream, json, cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                try
                {
                    if (stream.IsConnected) stream.WaitForPipeDrain();
                }
                catch (IOException)
                {
                    // The client already disconnected after reading.
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private void LogRequest(BridgeRequest request, BridgeResponse response, bool control, bool written)
        {
            if (request == null) return;
            if (control && response.Ok && (request.Op == ControlOps.Health || request.Op == ControlOps.Snapshot || request.Op == ControlOps.Hello))
            {
                return;
            }
            DiagnosticsLogger.Event("request", new Dictionary<string, object>
            {
                ["reqId"] = request.RequestId,
                ["op"] = request.Op,
                ["kind"] = request.Kind,
                ["clientKey"] = request.ClientKey,
                ["writeTag"] = request.WriteTag,
                ["pipe"] = control ? "control" : "primary",
                ["queueWaitMs"] = response.Metrics?.QueueWaitMs,
                ["raiseToExecMs"] = response.Metrics?.RaiseToExecMs,
                ["execMs"] = response.Metrics?.ExecMs,
                ["via"] = response.Metrics?.Via,
                ["code"] = response.Ok ? null : response.Code,
                ["written"] = written
            });
        }

        private NamedPipeServerStream CreateServerStream(bool control)
        {
            string name = control ? _controlPipeName : _pipeName;
            int maxInstances = control ? BridgeProtocol.ControlPipeInstances : BridgeProtocol.PrimaryPipeInstances;
#if NETFRAMEWORK
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, _pipeSecurity);
#else
            return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, _pipeSecurity);
#endif
        }

        private bool Track(NamedPipeServerStream stream)
        {
            lock (_lifecycleGate)
            {
                if (_disposed != 0) return false;
                _streams.Add(stream);
                return true;
            }
        }

        private void Untrack(NamedPipeServerStream stream)
        {
            if (stream == null) return;
            lock (_lifecycleGate) _streams.Remove(stream);
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
                // Best effort during fault recovery and shutdown.
            }
        }
    }
}
