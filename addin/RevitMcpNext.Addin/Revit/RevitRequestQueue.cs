using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class RevitRequestQueue
    {
        private readonly ConcurrentQueue<QueuedRevitWorkItem> _queue = new ConcurrentQueue<QueuedRevitWorkItem>();
        private ExternalEvent _externalEvent;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _cancelledCount;
        private long _raiseCount;
        private long _raiseNotAcceptedCount;
        private DateTimeOffset? _lastEnqueuedAtUtc;
        private DateTimeOffset? _lastDequeuedAtUtc;
        private DateTimeOffset? _lastCancelledAtUtc;
        private DateTimeOffset? _lastRaiseAtUtc;
        private string _lastRaiseResult = "not-attached";

        public event Action<BridgeRequestEnvelope> RequestStarted;

        public void AttachExternalEvent(ExternalEvent externalEvent)
        {
            _externalEvent = externalEvent;
        }

        public Task<BridgeResponseEnvelope> EnqueueAsync(BridgeRequestEnvelope envelope, CancellationToken cancellationToken)
        {
            var item = new QueuedRevitWorkItem(envelope, cancellationToken);
            Interlocked.Increment(ref _enqueuedCount);
            _lastEnqueuedAtUtc = item.EnqueuedAtUtc;
            _queue.Enqueue(item);
            RaiseExternalEvent();

            return item.Completion.Task;
        }

        public bool TryDequeue(out QueuedRevitWorkItem item)
        {
            while (_queue.TryDequeue(out item))
            {
                if (item.TryBeginExecution())
                {
                    Interlocked.Increment(ref _dequeuedCount);
                    _lastDequeuedAtUtc = DateTimeOffset.UtcNow;
                    RequestStarted?.Invoke(item.Envelope);
                    return true;
                }
                item.Dispose();
            }

            item = null;
            return false;
        }

        public bool HasPending => !_queue.IsEmpty;

        public void Raise()
        {
            RaiseExternalEvent();
        }

        private void RaiseExternalEvent()
        {
            Interlocked.Increment(ref _raiseCount);
            _lastRaiseAtUtc = DateTimeOffset.UtcNow;
            if (_externalEvent == null)
            {
                _lastRaiseResult = "not-attached";
                Interlocked.Increment(ref _raiseNotAcceptedCount);
                DiagnosticsLogger.Info("Revit MCP request queued before ExternalEvent was attached.");
                return;
            }

            ExternalEventRequest result = _externalEvent.Raise();
            _lastRaiseResult = result.ToString();
            if (result != ExternalEventRequest.Accepted && result != ExternalEventRequest.Pending)
            {
                Interlocked.Increment(ref _raiseNotAcceptedCount);
                DiagnosticsLogger.Info("Revit MCP ExternalEvent raise returned " + result + ". Revit may be busy or blocked by a modal dialog.");
            }
        }

        public bool TryCancelQueued(string requestId, string reason, string sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return false;

            foreach (QueuedRevitWorkItem item in _queue)
            {
                if (item.IsCancelled) continue;
                if (!string.Equals(item.Envelope.RequestId, requestId, StringComparison.Ordinal)) continue;
                if (sessionId != null &&
                    !string.Equals(item.Envelope.SessionId ?? string.Empty, sessionId, StringComparison.Ordinal)) continue;

                if (item.TryCancel("REQUEST_CANCELLED", string.IsNullOrWhiteSpace(reason)
                    ? "The queued request was cancelled before Revit processed it."
                    : "The queued request was cancelled before Revit processed it: " + reason))
                {
                    Interlocked.Increment(ref _cancelledCount);
                    _lastCancelledAtUtc = DateTimeOffset.UtcNow;
                    return true;
                }
            }

            return false;
        }

        public void CancelAll(string code, string message)
        {
            while (_queue.TryDequeue(out QueuedRevitWorkItem item))
            {
                if (item.TrySetResult(Failure(item.Envelope, code, message)))
                {
                    Interlocked.Increment(ref _cancelledCount);
                    _lastCancelledAtUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        public Dictionary<string, object> GetDiagnosticsSnapshot()
        {
            QueuedRevitWorkItem[] pending = _queue
                .Where(item => item.State == QueuedRevitWorkState.Pending)
                .ToArray();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            QueuedRevitWorkItem oldest = pending.OrderBy(item => item.EnqueuedAtUtc).FirstOrDefault();

            var snapshot = new Dictionary<string, object>
            {
                ["pendingCount"] = pending.Length,
                ["hasPending"] = pending.Length > 0,
                ["enqueuedCount"] = Interlocked.Read(ref _enqueuedCount),
                ["dequeuedCount"] = Interlocked.Read(ref _dequeuedCount),
                ["cancelledCount"] = Interlocked.Read(ref _cancelledCount),
                ["raiseCount"] = Interlocked.Read(ref _raiseCount),
                ["raiseNotAcceptedCount"] = Interlocked.Read(ref _raiseNotAcceptedCount),
                ["lastRaiseResult"] = _lastRaiseResult,
                ["externalEventAttached"] = _externalEvent != null
            };

            AddTimestamp(snapshot, "lastEnqueuedAtUtc", _lastEnqueuedAtUtc);
            AddTimestamp(snapshot, "lastDequeuedAtUtc", _lastDequeuedAtUtc);
            AddTimestamp(snapshot, "lastCancelledAtUtc", _lastCancelledAtUtc);
            AddTimestamp(snapshot, "lastRaiseAtUtc", _lastRaiseAtUtc);

            if (oldest != null)
            {
                snapshot["oldestPendingRequestId"] = oldest.Envelope.RequestId;
                snapshot["oldestPendingOperation"] = oldest.Envelope.Operation;
                snapshot["oldestPendingAgeMs"] = Math.Max(0, (long)(now - oldest.EnqueuedAtUtc).TotalMilliseconds);
            }

            return snapshot;
        }

        private static void AddTimestamp(Dictionary<string, object> snapshot, string key, DateTimeOffset? value)
        {
            if (value.HasValue)
            {
                snapshot[key] = value.Value.ToUniversalTime().ToString("o");
            }
        }

        private static BridgeResponseEnvelope Failure(BridgeRequestEnvelope request, string code, string message)
        {
            return new BridgeResponseEnvelope
            {
                Ok = false,
                RequestId = request.RequestId,
                Error = new BridgeError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true
                }
            };
        }

    }
}
