using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal enum QueuedRevitWorkState
    {
        Pending = 0,
        Running = 1,
        Cancelled = 2,
        Completed = 3
    }

    internal sealed class QueuedRevitWorkItem : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly long _enqueuedTimestamp;
        private long _startedTimestamp;
        private int _state = (int)QueuedRevitWorkState.Pending;

        public QueuedRevitWorkItem(BridgeRequestEnvelope envelope, CancellationToken cancellationToken)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            _enqueuedTimestamp = Stopwatch.GetTimestamp();
            EnqueuedAtUtc = DateTimeOffset.UtcNow;
            _cancellationRegistration = cancellationToken.Register(() =>
                TryCancel("REQUEST_CANCELLED", "The request was cancelled before Revit processed it."));
        }

        public BridgeRequestEnvelope Envelope { get; }
        public DateTimeOffset EnqueuedAtUtc { get; }
        public QueuedRevitWorkState State => (QueuedRevitWorkState)Volatile.Read(ref _state);
        public bool IsCancelled => State == QueuedRevitWorkState.Cancelled;
        public TaskCompletionSource<BridgeResponseEnvelope> Completion { get; } =
            new TaskCompletionSource<BridgeResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryBeginExecution()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)QueuedRevitWorkState.Running,
                    (int)QueuedRevitWorkState.Pending) != (int)QueuedRevitWorkState.Pending)
            {
                return false;
            }

            Interlocked.Exchange(ref _startedTimestamp, Stopwatch.GetTimestamp());
            return true;
        }

        public bool TryCancel(string code, string message)
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)QueuedRevitWorkState.Cancelled,
                    (int)QueuedRevitWorkState.Pending) != (int)QueuedRevitWorkState.Pending)
            {
                return false;
            }

            Completion.TrySetResult(Failure(Envelope, code, message));
            Dispose();
            return true;
        }

        public bool TrySetResult(BridgeResponseEnvelope response, long? revitExecutionMs = null)
        {
            while (true)
            {
                QueuedRevitWorkState state = State;
                if (state == QueuedRevitWorkState.Cancelled || state == QueuedRevitWorkState.Completed)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _state,
                        (int)QueuedRevitWorkState.Completed,
                        (int)state) != (int)state)
                {
                    continue;
                }

                if (revitExecutionMs.HasValue)
                {
                    BridgeMetrics metrics = response.Metrics ?? new BridgeMetrics();
                    response.Metrics = metrics;
                    metrics.QueueWaitMs = GetQueueWaitMs();
                    metrics.RevitExecutionMs = Math.Max(0, revitExecutionMs.Value);
                }

                Completion.TrySetResult(response);
                Dispose();
                return true;
            }
        }

        public void Dispose()
        {
            _cancellationRegistration.Dispose();
        }

        private long GetQueueWaitMs()
        {
            long startedTimestamp = Interlocked.Read(ref _startedTimestamp);
            if (startedTimestamp <= _enqueuedTimestamp) return 0;
            return (long)((startedTimestamp - _enqueuedTimestamp) * 1000.0 / Stopwatch.Frequency);
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
