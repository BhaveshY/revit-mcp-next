using System;
using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class RevitExternalEventHandler : IExternalEventHandler
    {
        private const int MaxItemsPerExternalEvent = 16;
        private readonly RevitRequestQueue _queue;
        private readonly TransactionService _transactions;

        public RevitExternalEventHandler(RevitRequestQueue queue, TransactionService transactions)
        {
            _queue = queue;
            _transactions = transactions;
        }

        public void Execute(UIApplication app)
        {
            int processed = 0;
            while (processed < MaxItemsPerExternalEvent && _queue.TryDequeue(out RevitRequestQueue.WorkItem item))
            {
                processed++;
                item.TrySetResult(Handle(app, item.Envelope));
            }
        }

        public string GetName()
        {
            return "Revit MCP Next External Event Handler";
        }

        private BridgeResponseEnvelope Handle(UIApplication app, BridgeRequestEnvelope request)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (app.ActiveUIDocument == null)
                {
                    return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before using Revit MCP Next.");
                }

                // First vertical slice placeholder. Real command dispatch lands with generated contracts.
                string dataJson = "{\"connected\":true,\"message\":\"addin queue is alive\"}";
                return new BridgeResponseEnvelope
                {
                    Ok = true,
                    RequestId = request.RequestId,
                    DataJson = dataJson,
                    Metrics = new BridgeMetrics { ElapsedMs = sw.ElapsedMilliseconds }
                };
            }
            catch (Exception ex)
            {
                return Failure(request, "REVIT_COMMAND_FAILED", ex.Message);
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

