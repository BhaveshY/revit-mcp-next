using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
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
            var item = new WorkItem(envelope);
            _queue.Enqueue(item);
            _externalEvent?.Raise();

            cancellationToken.Register(() =>
            {
                item.TrySetResult(Failure(envelope, "REQUEST_CANCELLED", "The request was cancelled before Revit processed it."));
            });

            return item.Completion.Task;
        }

        public bool TryDequeue(out WorkItem item)
        {
            return _queue.TryDequeue(out item);
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

        internal sealed class WorkItem
        {
            public WorkItem(BridgeRequestEnvelope envelope)
            {
                Envelope = envelope;
            }

            public BridgeRequestEnvelope Envelope { get; }
            public TaskCompletionSource<BridgeResponseEnvelope> Completion { get; } =
                new TaskCompletionSource<BridgeResponseEnvelope>();

            public void TrySetResult(BridgeResponseEnvelope response)
            {
                Completion.TrySetResult(response);
            }
        }
    }
}

