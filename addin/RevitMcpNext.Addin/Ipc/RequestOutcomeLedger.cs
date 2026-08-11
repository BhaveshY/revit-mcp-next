using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    internal enum RequestOutcomeState
    {
        Accepted,
        Running,
        Committed,
        RolledBack,
        Failed
    }

    internal sealed class RequestOutcomeLedger
    {
        public const int DefaultCapacity = 256;
        public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(15);

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int _capacity;
        private readonly TimeSpan _timeToLive;

        public RequestOutcomeLedger()
            : this(DefaultCapacity, DefaultTimeToLive)
        {
        }

        internal RequestOutcomeLedger(int capacity, TimeSpan timeToLive)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (timeToLive <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeToLive));
            _capacity = capacity;
            _timeToLive = timeToLive;
        }

        public RequestOutcomeLease Acquire(BridgeRequestEnvelope request, string requestFingerprint)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(requestFingerprint)) throw new ArgumentException("A request fingerprint is required.", nameof(requestFingerprint));
            string key = Key(request.SessionId, request.RequestId);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            lock (_gate)
            {
                RemoveExpiredUnsafe(now);
                if (_entries.TryGetValue(key, out Entry existing))
                {
                    if (!string.Equals(existing.Operation, request.Operation, StringComparison.Ordinal) ||
                        !string.Equals(existing.OperationKind, request.OperationKind, StringComparison.Ordinal) ||
                        !string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                    {
                        return RequestOutcomeLease.Conflict(
                            "Request ID '" + request.RequestId + "' is already associated with operation '" +
                            existing.Operation + "'.");
                    }

                    return RequestOutcomeLease.Replay(existing.Completion.Task);
                }

                TrimCompletedUnsafe();
                if (_entries.Count >= _capacity)
                {
                    return RequestOutcomeLease.Capacity(
                        "The request outcome ledger is full. Retry after an in-flight write completes.");
                }

                var entry = new Entry(
                    request.SessionId ?? string.Empty,
                    request.RequestId,
                    request.Operation,
                    request.OperationKind,
                    requestFingerprint,
                    now);
                _entries.Add(key, entry);
                return RequestOutcomeLease.Owner(entry.Completion.Task);
            }
        }

        public void MarkRunning(string sessionId, string requestId)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(Key(sessionId, requestId), out Entry entry) &&
                    entry.State == RequestOutcomeState.Accepted)
                {
                    entry.State = RequestOutcomeState.Running;
                }
            }
        }

        public void Complete(
            string sessionId,
            string requestId,
            BridgeResponseEnvelope response,
            RequestOutcomeState state)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (state != RequestOutcomeState.Committed &&
                state != RequestOutcomeState.RolledBack &&
                state != RequestOutcomeState.Failed)
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            TaskCompletionSource<RequestOutcome> completion = null;
            RequestOutcome outcome = null;
            lock (_gate)
            {
                if (!_entries.TryGetValue(Key(sessionId, requestId), out Entry entry)) return;
                if (IsTerminal(entry.State)) return;

                entry.State = state;
                entry.Response = response;
                entry.CompletedAtUtc = DateTimeOffset.UtcNow;
                outcome = entry.Snapshot();
                completion = entry.Completion;
            }

            completion.TrySetResult(outcome);
        }

        public bool TryGet(string sessionId, string requestId, out RequestOutcome outcome)
        {
            lock (_gate)
            {
                RemoveExpiredUnsafe(DateTimeOffset.UtcNow);
                if (_entries.TryGetValue(Key(sessionId, requestId), out Entry entry))
                {
                    outcome = entry.Snapshot();
                    return true;
                }
            }

            outcome = null;
            return false;
        }

        public Dictionary<string, object> GetDiagnosticsSnapshot()
        {
            lock (_gate)
            {
                RemoveExpiredUnsafe(DateTimeOffset.UtcNow);
                int completed = _entries.Values.Count(entry => IsTerminal(entry.State));
                return new Dictionary<string, object>
                {
                    ["activeCount"] = _entries.Count,
                    ["inFlightCount"] = _entries.Count - completed,
                    ["completedCount"] = completed,
                    ["capacity"] = _capacity,
                    ["ttlSeconds"] = (int)Math.Round(_timeToLive.TotalSeconds)
                };
            }
        }

        private void RemoveExpiredUnsafe(DateTimeOffset now)
        {
            foreach (string key in _entries
                .Where(pair => IsTerminal(pair.Value.State) &&
                               pair.Value.CompletedAtUtc.HasValue &&
                               now - pair.Value.CompletedAtUtc.Value >= _timeToLive)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _entries.Remove(key);
            }
        }

        private void TrimCompletedUnsafe()
        {
            int removeCount = _entries.Count - _capacity + 1;
            if (removeCount <= 0) return;

            foreach (string key in _entries
                .Where(pair => IsTerminal(pair.Value.State))
                .OrderBy(pair => pair.Value.CompletedAtUtc ?? pair.Value.AcceptedAtUtc)
                .Take(removeCount)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _entries.Remove(key);
            }
        }

        private static bool IsTerminal(RequestOutcomeState state)
        {
            return state == RequestOutcomeState.Committed ||
                   state == RequestOutcomeState.RolledBack ||
                   state == RequestOutcomeState.Failed;
        }

        private static string Key(string sessionId, string requestId)
        {
            return (sessionId ?? string.Empty) + "\0" + (requestId ?? string.Empty);
        }

        private sealed class Entry
        {
            public Entry(
                string sessionId,
                string requestId,
                string operation,
                string operationKind,
                string requestFingerprint,
                DateTimeOffset acceptedAtUtc)
            {
                SessionId = sessionId;
                RequestId = requestId;
                Operation = operation;
                OperationKind = operationKind;
                RequestFingerprint = requestFingerprint;
                AcceptedAtUtc = acceptedAtUtc;
            }

            public string SessionId { get; }
            public string RequestId { get; }
            public string Operation { get; }
            public string OperationKind { get; }
            public string RequestFingerprint { get; }
            public DateTimeOffset AcceptedAtUtc { get; }
            public DateTimeOffset? CompletedAtUtc { get; set; }
            public RequestOutcomeState State { get; set; } = RequestOutcomeState.Accepted;
            public BridgeResponseEnvelope Response { get; set; }
            public TaskCompletionSource<RequestOutcome> Completion { get; } =
                new TaskCompletionSource<RequestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

            public RequestOutcome Snapshot()
            {
                return new RequestOutcome(
                    SessionId,
                    RequestId,
                    Operation,
                    State,
                    Response,
                    AcceptedAtUtc,
                    CompletedAtUtc);
            }
        }
    }

    internal sealed class RequestOutcomeLease
    {
        private RequestOutcomeLease(
            bool isOwner,
            Task<RequestOutcome> completion,
            string errorCode,
            string errorMessage)
        {
            IsOwner = isOwner;
            Completion = completion;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public bool IsOwner { get; }
        public Task<RequestOutcome> Completion { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }
        public bool IsError => !string.IsNullOrWhiteSpace(ErrorCode);

        public static RequestOutcomeLease Owner(Task<RequestOutcome> completion)
        {
            return new RequestOutcomeLease(true, completion, null, null);
        }

        public static RequestOutcomeLease Replay(Task<RequestOutcome> completion)
        {
            return new RequestOutcomeLease(false, completion, null, null);
        }

        public static RequestOutcomeLease Conflict(string message)
        {
            return new RequestOutcomeLease(false, null, "REQUEST_ID_CONFLICT", message);
        }

        public static RequestOutcomeLease Capacity(string message)
        {
            return new RequestOutcomeLease(false, null, "REQUEST_LEDGER_CAPACITY", message);
        }
    }

    internal sealed class RequestOutcome
    {
        public RequestOutcome(
            string sessionId,
            string requestId,
            string operation,
            RequestOutcomeState state,
            BridgeResponseEnvelope response,
            DateTimeOffset acceptedAtUtc,
            DateTimeOffset? completedAtUtc)
        {
            SessionId = sessionId;
            RequestId = requestId;
            Operation = operation;
            State = state;
            Response = response;
            AcceptedAtUtc = acceptedAtUtc;
            CompletedAtUtc = completedAtUtc;
        }

        public string SessionId { get; }
        public string RequestId { get; }
        public string Operation { get; }
        public RequestOutcomeState State { get; }
        public BridgeResponseEnvelope Response { get; }
        public DateTimeOffset AcceptedAtUtc { get; }
        public DateTimeOffset? CompletedAtUtc { get; }
    }
}
