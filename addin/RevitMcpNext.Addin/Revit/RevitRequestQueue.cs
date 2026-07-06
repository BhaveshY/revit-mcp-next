using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class RevitRequestQueue
    {
        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        private ExternalEvent _externalEvent;

        public void AttachExternalEvent(ExternalEvent externalEvent)
        {
            _externalEvent = externalEvent;
        }

        public Task<BridgeResponseEnvelope> EnqueueAsync(BridgeRequestEnvelope envelope, CancellationToken cancellationToken)
        {
            var item = new WorkItem(envelope, cancellationToken);
            _queue.Enqueue(item);
            RaiseExternalEvent();

            return item.Completion.Task;
        }

        public bool TryDequeue(out WorkItem item)
        {
            while (_queue.TryDequeue(out item))
            {
                if (!item.IsCancelled) return true;
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
            if (_externalEvent == null)
            {
                DiagnosticsLogger.Info("Revit MCP request queued before ExternalEvent was attached.");
                return;
            }

            ExternalEventRequest result = _externalEvent.Raise();
            if (result != ExternalEventRequest.Accepted && result != ExternalEventRequest.Pending)
            {
                DiagnosticsLogger.Info("Revit MCP ExternalEvent raise returned " + result + ". Revit may be busy or blocked by a modal dialog.");
            }
        }

        public void CancelAll(string code, string message)
        {
            while (_queue.TryDequeue(out WorkItem item))
            {
                item.TrySetResult(Failure(item.Envelope, code, message));
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

        internal sealed class WorkItem : IDisposable
        {
            private readonly CancellationTokenRegistration _cancellationRegistration;
            private int _completed;

            public WorkItem(BridgeRequestEnvelope envelope, CancellationToken cancellationToken)
            {
                Envelope = envelope;
                _cancellationRegistration = cancellationToken.Register(() =>
                {
                    IsCancelled = true;
                    TrySetResult(Failure(envelope, "REQUEST_CANCELLED", "The request was cancelled before Revit processed it."));
                });
            }

            public BridgeRequestEnvelope Envelope { get; }
            public bool IsCancelled { get; private set; }
            public TaskCompletionSource<BridgeResponseEnvelope> Completion { get; } =
                new TaskCompletionSource<BridgeResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

            public void TrySetResult(BridgeResponseEnvelope response)
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    Completion.TrySetResult(response);
                    Dispose();
                }
            }

            public void Dispose()
            {
                _cancellationRegistration.Dispose();
            }
        }
    }
}
