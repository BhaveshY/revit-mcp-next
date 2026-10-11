using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The queue between pipe threads and the Revit UI thread (SPEC §8.3, §9.4; D2 §5.4/§5.6).
    /// <list type="bullet">
    /// <item>Admission: at most <see cref="MaxPending"/> pending items and <see cref="MaxPendingPerClient"/> per clientKey;
    /// anything above is refused at once with REVIT_QUEUE_FULL (<see cref="TryEnqueue"/>), so saturation never outlives the
    /// real work it represents.</item>
    /// <item>Dequeue is round-robin per clientKey (the client served least recently goes first) and FIFO within a client;
    /// a filter (the Idling fallback runs idle-safe ops only) never lets a client's later item overtake its first one.</item>
    /// <item>Execution tracking for health/snapshot, cooperative cancel, and deferred completion: while an item's completion
    /// is deferred no other item starts.</item>
    /// </list>
    /// </summary>
    internal sealed class RevitRequestQueue
    {
        /// <summary>Pending items above which new requests are refused (D2 §20 MAX_PENDING).</summary>
        public const int MaxPending = 32;
        /// <summary>Pending items per clientKey above which that client's new requests are refused (D2 §20 MAX_PER_CLIENT).</summary>
        public const int MaxPendingPerClient = 8;
        private const int MaxServedEntries = 128;

        private readonly object _gate = new object();
        private readonly LinkedList<QueuedRevitWorkItem> _pending = new LinkedList<QueuedRevitWorkItem>();
        private readonly Dictionary<string, long> _servedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        private ExternalEventPump _pump;
        private ExecutingInfo _executing;
        private QueuedRevitWorkItem _current;
        private QueuedRevitWorkItem _deferred;
        private long _enqueued;
        private long _dequeued;
        private long _cancelled;
        private long _rejected;
        private long _serveSeq;

        /// <summary>Raised on the UI thread when an item starts executing.</summary>
        public event Action<QueuedRevitWorkItem> RequestStarted;

        public void AttachPump(ExternalEventPump pump)
        {
            _pump = pump;
        }

        /// <summary>Requests refused by admission control since startup (health.admissionRejections).</summary>
        public long AdmissionRejections => Interlocked.Read(ref _rejected);

        /// <summary>Queues a request; the task completes with its response (or a cancellation failure).</summary>
        public Task<BridgeResponse> EnqueueAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            return Enqueue(request, cancellationToken).Completion.Task;
        }

        /// <summary>Queues a request without admission control (internal callers).</summary>
        public QueuedRevitWorkItem Enqueue(BridgeRequest request, CancellationToken cancellationToken)
        {
            var item = new QueuedRevitWorkItem(request, cancellationToken);
            if (item.IsCancelled) return item;
            lock (_gate)
            {
                _pending.AddLast(item);
                _enqueued++;
            }
            item.MarkRaised();
            _pump?.OnEnqueue();
            return item;
        }

        /// <summary>
        /// Admission-controlled enqueue (pipe host). Returns null and the REVIT_QUEUE_FULL response when the queue holds
        /// <see cref="MaxPending"/> items or the client already has <see cref="MaxPendingPerClient"/>.
        /// </summary>
        public QueuedRevitWorkItem TryEnqueue(BridgeRequest request, CancellationToken cancellationToken, out BridgeResponse rejection)
        {
            rejection = null;
            if (request == null) throw new ArgumentNullException(nameof(request));
            string client = ClientOf(request.ClientKey);
            var item = new QueuedRevitWorkItem(request, cancellationToken);
            if (item.IsCancelled) return item;
            bool admitted;
            lock (_gate)
            {
                CountPendingUnsafe(client, out int pending, out int mine);
                admitted = pending < MaxPending && mine < MaxPendingPerClient;
                if (admitted)
                {
                    _pending.AddLast(item);
                    _enqueued++;
                }
                else
                {
                    _rejected++;
                    rejection = QueueFullUnsafe(request, client, pending, mine);
                }
            }

            if (!admitted)
            {
                item.Dispose();
                DiagnosticsLogger.Info("queue", "admission refused " + request.Op + " for " + client + " (REVIT_QUEUE_FULL).",
                    new Dictionary<string, object> { ["reqId"] = request.RequestId, ["details"] = rejection.Details });
                return null;
            }
            item.MarkRaised();
            _pump?.OnEnqueue();
            return item;
        }

        /// <summary>True when a new request of this client would be admitted now (cheap pre-check; TryEnqueue decides).</summary>
        public bool CanAdmit(BridgeRequest request, out BridgeResponse rejection)
        {
            rejection = null;
            string client = ClientOf(request?.ClientKey);
            lock (_gate)
            {
                CountPendingUnsafe(client, out int pending, out int mine);
                if (pending < MaxPending && mine < MaxPendingPerClient) return true;
                _rejected++;
                rejection = QueueFullUnsafe(request, client, pending, mine);
            }
            DiagnosticsLogger.Info("queue", "admission refused " + request?.Op + " for " + client + " (REVIT_QUEUE_FULL).",
                new Dictionary<string, object> { ["reqId"] = request?.RequestId, ["details"] = rejection.Details });
            return false;
        }

        /// <summary>
        /// Takes the next pending item (UI thread): round-robin over clients (least recently served first), FIFO within a
        /// client. Returns false while a deferred completion is outstanding. <paramref name="filter"/> rejects items (e.g.
        /// non-idle-safe ops in the Idling fallback); a rejected first item holds back the rest of its client's items.
        /// </summary>
        public bool TryDequeue(out QueuedRevitWorkItem item, Func<QueuedRevitWorkItem, bool> filter = null)
        {
            item = null;
            lock (_gate)
            {
                if (_deferred != null) return false;
                while (true)
                {
                    LinkedListNode<QueuedRevitWorkItem> chosen = null;
                    long chosenServed = long.MaxValue;
                    var seenClients = new HashSet<string>(StringComparer.Ordinal);
                    LinkedListNode<QueuedRevitWorkItem> node = _pending.First;
                    while (node != null)
                    {
                        LinkedListNode<QueuedRevitWorkItem> next = node.Next;
                        QueuedRevitWorkItem candidate = node.Value;
                        if (!candidate.IsPending)
                        {
                            _pending.Remove(node);
                            if (candidate.IsCancelled) _cancelled++;
                            node = next;
                            continue;
                        }
                        string client = ClientOf(candidate.ClientKey);
                        if (seenClients.Add(client))
                        {
                            // Only the client's first pending item is a candidate (FIFO within a client).
                            bool eligible = filter == null || SafeFilter(filter, candidate);
                            if (eligible)
                            {
                                long served = _servedAt.TryGetValue(client, out long at) ? at : -1;
                                if (chosen == null || served < chosenServed)
                                {
                                    chosen = node;
                                    chosenServed = served;
                                }
                            }
                        }
                        node = next;
                    }

                    if (chosen == null) break;
                    QueuedRevitWorkItem picked = chosen.Value;
                    _pending.Remove(chosen);
                    if (picked.TryBeginExecution())
                    {
                        item = picked;
                        _dequeued++;
                        _servedAt[ClientOf(picked.ClientKey)] = ++_serveSeq;
                        PruneServedUnsafe();
                        break;
                    }
                    if (picked.IsCancelled) _cancelled++;
                }
            }

            if (item == null) return false;
            try
            {
                RequestStarted?.Invoke(item);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("queue", "A RequestStarted subscriber failed.", ex);
            }
            return true;
        }

        public bool HasPending
        {
            get
            {
                lock (_gate)
                {
                    foreach (QueuedRevitWorkItem item in _pending)
                    {
                        if (item.IsPending) return true;
                    }
                    return false;
                }
            }
        }

        public int PendingCount
        {
            get
            {
                lock (_gate) return _pending.Count(item => item.IsPending);
            }
        }

        /// <summary>Age of the oldest pending item in ms (0 when none).</summary>
        public long OldestPendingAgeMs
        {
            get
            {
                lock (_gate)
                {
                    QueuedRevitWorkItem oldest = _pending.FirstOrDefault(item => item.IsPending);
                    return oldest?.AgeMs ?? 0;
                }
            }
        }

        public QueuedRevitWorkItem OldestPending
        {
            get
            {
                lock (_gate) return _pending.FirstOrDefault(item => item.IsPending);
            }
        }

        /// <summary>True while an item or job step runs on the UI thread (including deferred completions).</summary>
        public bool IsExecuting
        {
            get
            {
                lock (_gate) return _executing != null || _deferred != null;
            }
        }

        /// <summary>The executing item (request or job step) for health and snapshot; null when idle.</summary>
        public ExecutingInfo Executing
        {
            get
            {
                lock (_gate)
                {
                    if (_executing == null) return null;
                    return new ExecutingInfo
                    {
                        RequestId = _executing.RequestId,
                        Op = _executing.Op,
                        SinceUtc = _executing.SinceUtc,
                        ClientKey = _executing.ClientKey,
                        WriteTag = _executing.WriteTag,
                        JobId = _executing.JobId,
                        ElapsedMs = ElapsedSince(_executing.SinceUtc)
                    };
                }
            }
        }

        /// <summary>The running queue item (null for job steps or when idle).</summary>
        public QueuedRevitWorkItem Current
        {
            get { lock (_gate) return _current; }
        }

        /// <summary>The item whose completion is deferred, or null.</summary>
        public QueuedRevitWorkItem Deferred
        {
            get { lock (_gate) return _deferred; }
        }

        /// <summary>Marks an item (or a job step when item is null) as executing on the UI thread.</summary>
        public void BeginExecution(QueuedRevitWorkItem item, ExecutingInfo info)
        {
            lock (_gate)
            {
                _current = item;
                _executing = info;
            }
            McpRuntime.Registry?.SetExecuting(info);
        }

        public void EndExecution()
        {
            bool stillDeferred;
            lock (_gate)
            {
                _current = null;
                _executing = null;
                stillDeferred = _deferred != null;
            }
            if (!stillDeferred) McpRuntime.Registry?.ClearExecuting();
        }

        /// <summary>Holds the queue until the item's deferred task completes (see RequestContext.DeferCompletion).</summary>
        public void SetDeferred(QueuedRevitWorkItem item)
        {
            lock (_gate) _deferred = item;
            _pump?.OnEnqueue();
        }

        public void ClearDeferred(QueuedRevitWorkItem item)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_deferred, item)) _deferred = null;
            }
            McpRuntime.Registry?.ClearExecuting();
        }

        /// <summary>
        /// cancel_request: "queued" (cancelled before it ran), "cooperative" (a running read was asked to yield),
        /// "running_write" (writes are never interrupted) or "not_found".
        /// </summary>
        public string TryCancel(string requestId, string reason = null)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return CancelOutcomes.NotFound;
            QueuedRevitWorkItem running = null;
            lock (_gate)
            {
                foreach (QueuedRevitWorkItem item in _pending)
                {
                    if (!string.Equals(item.RequestId, requestId, StringComparison.Ordinal)) continue;
                    if (item.TryCancel(ErrorCodes.RequestCancelled, string.IsNullOrWhiteSpace(reason)
                            ? "The queued request was cancelled before Revit processed it."
                            : "The queued request was cancelled before Revit processed it: " + reason))
                    {
                        _cancelled++;
                        return CancelOutcomes.Queued;
                    }
                }
                if (_current != null && string.Equals(_current.RequestId, requestId, StringComparison.Ordinal)) running = _current;
                else if (_deferred != null && string.Equals(_deferred.RequestId, requestId, StringComparison.Ordinal)) running = _deferred;
            }

            if (running == null) return CancelOutcomes.NotFound;
            string kind = running.Request.Kind;
            if (kind == RequestKinds.Read || kind == RequestKinds.Ui || kind == RequestKinds.Control)
            {
                running.CancelRequested = true;
                return CancelOutcomes.Cooperative;
            }
            return CancelOutcomes.RunningWrite;
        }

        /// <summary>Fails every pending item (shutdown or startup failure).</summary>
        public void CancelAll(string code, string message)
        {
            List<QueuedRevitWorkItem> items;
            lock (_gate)
            {
                items = _pending.ToList();
                _pending.Clear();
            }
            foreach (QueuedRevitWorkItem item in items)
            {
                if (item.TryCancel(code, message)) Interlocked.Increment(ref _cancelled);
            }
            QueuedRevitWorkItem deferred;
            lock (_gate)
            {
                deferred = _deferred;
                _deferred = null;
            }
            deferred?.TrySetResult(BridgeResponse.Failure(deferred.RequestId, code, message));
        }

        public QueueHealth GetHealth()
        {
            var health = new QueueHealth { Executing = Executing };
            lock (_gate)
            {
                foreach (QueuedRevitWorkItem item in _pending)
                {
                    if (!item.IsPending) continue;
                    health.Pending++;
                    string key = ClientOf(item.ClientKey);
                    health.ByClient.TryGetValue(key, out int count);
                    health.ByClient[key] = count + 1;
                }
            }
            return health;
        }

        public Dictionary<string, object> GetCounters()
        {
            lock (_gate)
            {
                return new Dictionary<string, object>
                {
                    ["enqueued"] = _enqueued,
                    ["dequeued"] = _dequeued,
                    ["cancelled"] = _cancelled,
                    ["rejected"] = _rejected
                };
            }
        }

        private void CountPendingUnsafe(string client, out int pending, out int mine)
        {
            pending = 0;
            mine = 0;
            foreach (QueuedRevitWorkItem item in _pending)
            {
                if (!item.IsPending) continue;
                pending++;
                if (string.Equals(ClientOf(item.ClientKey), client, StringComparison.Ordinal)) mine++;
            }
        }

        private BridgeResponse QueueFullUnsafe(BridgeRequest request, string client, int pending, int mine)
        {
            var byClient = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (QueuedRevitWorkItem item in _pending)
            {
                if (!item.IsPending) continue;
                string key = ClientOf(item.ClientKey);
                byClient[key] = (byClient.TryGetValue(key, out object count) ? (int)count : 0) + 1;
            }
            ExecutingInfo executing = _executing;
            long executingForMs = executing == null ? 0 : ElapsedSince(executing.SinceUtc);
            var details = new Dictionary<string, object>
            {
                ["pending"] = pending,
                ["mine"] = mine,
                ["others"] = pending - mine,
                ["limit"] = MaxPending,
                ["perClientLimit"] = MaxPendingPerClient,
                ["executing"] = executing?.Op,
                ["executingOp"] = executing?.Op,
                ["executingForMs"] = executingForMs,
                ["byClient"] = byClient
            };
            string why = mine >= MaxPendingPerClient
                ? "this client already has " + mine.ToString(CultureInfo.InvariantCulture) + " requests waiting in Revit (limit " +
                  MaxPendingPerClient.ToString(CultureInfo.InvariantCulture) + ")"
                : "Revit already has " + pending.ToString(CultureInfo.InvariantCulture) + " requests waiting (limit " +
                  MaxPending.ToString(CultureInfo.InvariantCulture) + ")";
            string busy = executing == null
                ? string.Empty
                : "; it is executing " + executing.Op + " (" + (executingForMs / 1000).ToString(CultureInfo.InvariantCulture) + " s)";
            return BridgeResponse.Failure(request.RequestId, ErrorCodes.RevitQueueFull,
                "The add-in queue is full: " + why + busy + ". " + request.Op + " was not queued.", details);
        }

        private void PruneServedUnsafe()
        {
            if (_servedAt.Count <= MaxServedEntries) return;
            foreach (string key in _servedAt.OrderBy(pair => pair.Value).Take(_servedAt.Count - MaxServedEntries / 2).Select(pair => pair.Key).ToList())
            {
                _servedAt.Remove(key);
            }
        }

        private static bool SafeFilter(Func<QueuedRevitWorkItem, bool> filter, QueuedRevitWorkItem item)
        {
            try
            {
                return filter(item);
            }
            catch
            {
                return false;
            }
        }

        private static string ClientOf(string clientKey)
        {
            return string.IsNullOrEmpty(clientKey) ? "(unknown)" : clientKey;
        }

        private static long ElapsedSince(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc)) return 0;
            if (!DateTime.TryParse(isoUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime since))
            {
                return 0;
            }
            return Math.Max(0, (long)(DateTime.UtcNow - since).TotalMilliseconds);
        }
    }
}
