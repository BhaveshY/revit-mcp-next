using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    internal enum QueuedRevitWorkState
    {
        Pending = 0,
        Running = 1,
        Cancelled = 2,
        Completed = 3
    }

    /// <summary>
    /// One queued primary-pipe request. Completion is set exactly once: by the dispatcher, by cancellation while
    /// pending, by deferred completion, or by a job hand-off. Thread-safe state transitions.
    /// </summary>
    internal sealed class QueuedRevitWorkItem : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private long _startedTimestamp;
        private long _firstRaiseTimestamp;
        private int _state = (int)QueuedRevitWorkState.Pending;

        public QueuedRevitWorkItem(BridgeRequest request, CancellationToken cancellationToken)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            EnqueuedTimestamp = Stopwatch.GetTimestamp();
            EnqueuedAtUtc = DateTimeOffset.UtcNow;
            Token = cancellationToken;
            _cancellationRegistration = cancellationToken.Register(() =>
                TryCancel(ErrorCodes.RequestCancelled, "The request was cancelled before Revit processed it."));
        }

        public BridgeRequest Request { get; }
        public string RequestId => Request.RequestId;
        public string Op => Request.Op;
        public string ClientKey => Request.ClientKey ?? string.Empty;
        public string WriteTag => Request.WriteTag;
        public CancellationToken Token { get; }
        public DateTimeOffset EnqueuedAtUtc { get; }
        public long EnqueuedTimestamp { get; }
        public DateTimeOffset? StartedAtUtc { get; private set; }

        public QueuedRevitWorkState State => (QueuedRevitWorkState)Volatile.Read(ref _state);
        public bool IsPending => State == QueuedRevitWorkState.Pending;
        public bool IsCancelled => State == QueuedRevitWorkState.Cancelled;
        public bool IsCompleted => State == QueuedRevitWorkState.Completed || State == QueuedRevitWorkState.Cancelled;

        public TaskCompletionSource<BridgeResponse> Completion { get; } =
            new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Cooperative cancel for a running read (control cancel_request); handlers see ctx.CancelRequested.</summary>
        public volatile bool CancelRequested;

        /// <summary>The context of the running request (null while pending).</summary>
        public RequestContext Context { get; set; }

        /// <summary>Deferred completion (ctx.DeferCompletion): the item stays executing until the task completes.</summary>
        public Task<OpResult> DeferredTask { get; set; }
        public DateTime DeferredDeadlineUtc { get; set; }
        public Func<OpResult> DeferredOnTimeout { get; set; }

        /// <summary>Called by the pump the first time it raises the ExternalEvent for this item.</summary>
        public void MarkRaised()
        {
            Interlocked.CompareExchange(ref _firstRaiseTimestamp, Stopwatch.GetTimestamp(), 0);
        }

        public bool TryBeginExecution()
        {
            if (Interlocked.CompareExchange(ref _state, (int)QueuedRevitWorkState.Running, (int)QueuedRevitWorkState.Pending) !=
                (int)QueuedRevitWorkState.Pending)
            {
                return false;
            }
            Interlocked.Exchange(ref _startedTimestamp, Stopwatch.GetTimestamp());
            StartedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }

        public bool TryCancel(string code, string message)
        {
            if (Interlocked.CompareExchange(ref _state, (int)QueuedRevitWorkState.Cancelled, (int)QueuedRevitWorkState.Pending) !=
                (int)QueuedRevitWorkState.Pending)
            {
                return false;
            }
            BridgeResponse response = BridgeResponse.Failure(RequestId, code, message);
            response.Metrics = new ResponseMetrics { QueueWaitMs = ElapsedMs(EnqueuedTimestamp, Stopwatch.GetTimestamp()), Via = "queue" };
            Completion.TrySetResult(response);
            Dispose();
            return true;
        }

        /// <summary>Completes a running (or pending) item; false when it was already completed or cancelled.</summary>
        public bool TrySetResult(BridgeResponse response)
        {
            while (true)
            {
                QueuedRevitWorkState state = State;
                if (state == QueuedRevitWorkState.Cancelled || state == QueuedRevitWorkState.Completed) return false;
                if (Interlocked.CompareExchange(ref _state, (int)QueuedRevitWorkState.Completed, (int)state) != (int)state) continue;
                Completion.TrySetResult(response);
                Dispose();
                return true;
            }
        }

        /// <summary>Enqueue to execution start (or to now while pending).</summary>
        public long QueueWaitMs
        {
            get
            {
                long started = Interlocked.Read(ref _startedTimestamp);
                return started == 0 ? ElapsedMs(EnqueuedTimestamp, Stopwatch.GetTimestamp()) : ElapsedMs(EnqueuedTimestamp, started);
            }
        }

        /// <summary>First ExternalEvent raise to execution start (0 when unknown).</summary>
        public long RaiseToExecMs
        {
            get
            {
                long raised = Interlocked.Read(ref _firstRaiseTimestamp);
                long started = Interlocked.Read(ref _startedTimestamp);
                return raised == 0 || started == 0 || started < raised ? 0 : ElapsedMs(raised, started);
            }
        }

        public long AgeMs => ElapsedMs(EnqueuedTimestamp, Stopwatch.GetTimestamp());

        public void Dispose()
        {
            _cancellationRegistration.Dispose();
        }

        internal static long ElapsedMs(long fromTimestamp, long toTimestamp)
        {
            if (toTimestamp <= fromTimestamp) return 0;
            return (long)((toTimestamp - fromTimestamp) * 1000.0 / Stopwatch.Frequency);
        }
    }
}
