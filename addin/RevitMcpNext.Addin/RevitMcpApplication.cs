using System;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Addin.Revit;

namespace RevitMcpNext.Addin
{
    public sealed class RevitMcpApplication : IExternalApplication
    {
        private NamedPipeHost _pipeHost;
        private RevitRequestQueue _queue;
        private RevitExternalEventHandler _handler;
        private ExternalEvent _externalEvent;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                _queue = new RevitRequestQueue();
                _handler = new RevitExternalEventHandler(_queue, new TransactionService());
                _externalEvent = ExternalEvent.Create(_handler);

                _queue.AttachExternalEvent(_externalEvent);

                _pipeHost = new NamedPipeHost(
                    pipeName: PipeNameProvider.GetDefaultPipeName(),
                    requestQueue: _queue);
                _pipeHost.Start();

                return Result.Succeeded;
            }
            catch
            {
                // Never show a modal dialog during automation startup. Revit will surface load failure.
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                _pipeHost?.Dispose();
                _queue?.CancelAll("ADDIN_SHUTDOWN", "Revit is shutting down.");
                _externalEvent?.Dispose();
                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }
    }
}

