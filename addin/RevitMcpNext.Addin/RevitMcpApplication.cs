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
        private RuntimeInstanceRegistration _instanceRegistration;
        private string _runtimeInstanceId;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                _queue = new RevitRequestQueue();
                _generationTracker = new DocumentGenerationTracker();
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;
                application.DialogBoxShowing += OnDialogBoxShowing;

                _runtimeInstanceId = PipeNameProvider.CreateRuntimeInstanceId(application.ControlledApplication.VersionNumber);

                _handler = new RevitExternalEventHandler(
                    _queue,
                    new TransactionService(),
                    _generationTracker,
                    runtimeInstanceId: _runtimeInstanceId);
                _externalEvent = ExternalEvent.Create(_handler);
                RevitMcpInProcessBridge.Configure(_handler);

                _queue.AttachExternalEvent(_externalEvent);

                string pipeName = PipeNameProvider.GetRuntimePipeName(_runtimeInstanceId);
                PipeAuthOptions authOptions = PipeAuthOptions.FromEnvironment();
                _pipeHost = new NamedPipeHost(
                    pipeName: pipeName,
                    requestQueue: _queue,
                    authOptions: authOptions);
                _pipeHost.Start();
                _instanceRegistration = RuntimeInstanceRegistration.Start(
                    _runtimeInstanceId,
                    pipeName,
                    application.ControlledApplication.VersionNumber,
                    application.ControlledApplication.VersionBuild,
                    typeof(RevitMcpApplication).Assembly.GetName().Version?.ToString() ?? string.Empty);
                DiagnosticsLogger.Info(
                    "Revit MCP Next add-in instance " + _runtimeInstanceId + " started on pipe " + pipeName + ". Pipe ACL is restricted to the current Windows user. Auth token required=" + authOptions.IsRequired + ".");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                application.DialogBoxShowing -= OnDialogBoxShowing;
                _instanceRegistration?.Dispose();
                RevitMcpInProcessBridge.Clear(_handler);
                _pipeHost?.Dispose();
                _queue?.CancelAll("ADDIN_STARTUP_FAILED", "Revit MCP Next add-in startup failed.");
                _externalEvent?.Dispose();
                DiagnosticsLogger.Error("Revit MCP Next add-in startup failed.", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                application.DialogBoxShowing -= OnDialogBoxShowing;
                _instanceRegistration?.Dispose();
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

        private void OnDialogBoxShowing(object sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs args)
        {
            // Diagnostics only: never override the dialog result. The broker surfaces the
            // last dialog when Revit stops servicing queued MCP requests, so the agent can
            // tell the user which dialog to close instead of waiting for a timeout.
            try
            {
                if (args == null || _queue == null) return;
                string message = null;
                if (args is Autodesk.Revit.UI.Events.TaskDialogShowingEventArgs taskDialog) message = taskDialog.Message;
                else if (args is Autodesk.Revit.UI.Events.MessageBoxShowingEventArgs messageBox) message = messageBox.Message;
                _queue.RecordDialog(args.DialogId, message);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("Failed to record Revit dialog diagnostics.", ex);
            }
        }

        private static bool IsPreviewOnlyDocumentChange(DocumentChangedEventArgs args)
        {
            if (args == null) return false;
            if (args.Operation != UndoOperation.TransactionRolledBack) return false;

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
