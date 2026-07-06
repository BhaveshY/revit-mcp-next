using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
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
        private DocumentGenerationTracker _generationTracker;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                _queue = new RevitRequestQueue();
                _generationTracker = new DocumentGenerationTracker();
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;

                _handler = new RevitExternalEventHandler(_queue, new TransactionService(), _generationTracker);
                _externalEvent = ExternalEvent.Create(_handler);
                RevitMcpInProcessBridge.Configure(_handler);

                _queue.AttachExternalEvent(_externalEvent);

                string pipeName = PipeNameProvider.GetDefaultPipeName();
                PipeAuthOptions authOptions = PipeAuthOptions.FromEnvironment();
                _pipeHost = new NamedPipeHost(
                    pipeName: pipeName,
                    requestQueue: _queue,
                    authOptions: authOptions);
                _pipeHost.Start();
                DiagnosticsLogger.Info(
                    "Revit MCP Next add-in started on pipe " + pipeName + ". Pipe ACL is restricted to the current Windows user. Auth token required=" + authOptions.IsRequired + ".");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                DiagnosticsLogger.Error("Revit MCP Next add-in startup failed.", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                RevitMcpInProcessBridge.Clear(_handler);
                _pipeHost?.Dispose();
                _queue?.CancelAll("ADDIN_SHUTDOWN", "Revit is shutting down.");
                _externalEvent?.Dispose();
                DiagnosticsLogger.Info("Revit MCP Next add-in shut down.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("Revit MCP Next add-in shutdown failed.", ex);
                return Result.Failed;
            }
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs args)
        {
            try
            {
                if (IsPreviewOnlyDocumentChange(args))
                {
                    return;
                }

                Document document = args.GetDocument();
                if (document != null)
                {
                    _generationTracker?.MarkChanged(document);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("Failed to update document generation after Revit document change.", ex);
            }
        }

        private static bool IsPreviewOnlyDocumentChange(DocumentChangedEventArgs args)
        {
            if (args == null) return false;

            ICollection<string> transactionNames = args.GetTransactionNames();
            if (transactionNames == null || transactionNames.Count == 0) return false;

            foreach (string transactionName in transactionNames)
            {
                if (string.IsNullOrWhiteSpace(transactionName) ||
                    !transactionName.StartsWith("Revit MCP preview ", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
