using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The queue between pipe threads and the Revit UI thread (SPEC §9.4, D2 §5.4/§5.6). FIFO with an optional
    /// dequeue filter (Idling fallback runs idle-safe ops only), execution tracking for health/snapshot, cooperative
    /// cancel, and deferred completion: while an item's completion is deferred no other item starts.
    /// Admission control and per-client round-robin are P-REL-ADDIN's (wave 2).
    /// </summary>
    internal sealed class RevitRequestQueue
    {
        private readonly object _gate = new object();
        private readonly LinkedList<QueuedRevitWorkItem> _pending = new LinkedList<QueuedRevitWorkItem>();
        private ExternalEventPump _pump;
        private ExecutingInfo _executing;
        private QueuedRevitWorkItem _current;
        private QueuedRevitWorkItem _deferred;
        private long _enqueued;
        private long _dequeued;
        private long _cancelled;

        /// <summary>Raised on the UI thread when an item starts executing.</summary>
        public event Action<QueuedRevitWorkItem> RequestStarted;

        public void AttachPump(ExternalEventPump pump)
        {
            _pump = pump;
        }

        /// <summary>Queues a request; the task completes with its response (or a cancellation failure).</summary>
        public Task<BridgeResponse> EnqueueAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            return Enqueue(request, cancellationToken).Completion.Task;
        }

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
        /// Takes the next pending item (UI thread). Returns false while a deferred completion is outstanding.
        /// <paramref name="filter"/> skips (but keeps) items it rejects, e.g. non-idle-safe ops in the Idling fallback.
        /// </summary>
        public bool TryDequeue(out QueuedRevitWorkItem item, Func<QueuedRevitWorkItem, bool> filter = null)
        {
            item = null;
            lock (_gate)
            {
                if (_deferred != null) return false;
                LinkedListNode<QueuedRevitWorkItem> node = _pending.First;
                while (node != null)
                {
                    LinkedListNode<QueuedRevitWorkItem> next = node.Next;
                    QueuedRevitWorkItem candidate = node.Value;
                    if (!candidate.IsPending)
                    {
                        _pending.Remove(node);
                        if (candidate.IsCancelled) _cancelled++;
                    }
                    else if (filter == null || filter(candidate))
                    {
                        _pending.Remove(node);
                        if (candidate.TryBeginExecution())
                        {
                            item = candidate;
                            _dequeued++;
                            break;
                        }
                        if (candidate.IsCancelled) _cancelled++;
                    }
                    node = next;
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
                lock (_gate) return _pending.Any(item => item.IsPending);
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
                    string key = string.IsNullOrEmpty(item.ClientKey) ? "(unknown)" : item.ClientKey;
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
                    ["cancelled"] = _cancelled
                };
            }
        }

        private static long ElapsedSince(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc)) return 0;
            if (!DateTime.TryParse(isoUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime since))
            {
                return 0;
            }
            return Math.Max(0, (long)(DateTime.UtcNow - since).TotalMilliseconds);
        }
    }
}
