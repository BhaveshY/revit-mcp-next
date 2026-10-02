using System;
using Autodesk.Revit.UI.Events;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Dialog policy (SPEC §8.5, D2 §6). Wave-1 stub: records every DialogBoxShowing into health.lastDialog and the log
    /// and answers nothing. P-REL-ADDIN implements the Tier A/B allowlist, the denylist and REVIT_DIALOG_ANSWERED.
    /// Ids starting "RevitMcpNext_" (our own dialogs) must never be answered.
    /// </summary>
    internal sealed class DialogPolicy
    {
        private readonly object _gate = new object();
        private readonly Func<DialogPolicySettings> _settings;
        private DialogRecord _last;

        public DialogPolicy(Func<DialogPolicySettings> settings)
        {
            _settings = settings ?? (() => new DialogPolicySettings());
        }

        /// <summary>The last dialog Revit showed (health.lastDialog), or null.</summary>
        public DialogRecord LastDialog
        {
            get { lock (_gate) return _last; }
        }

        /// <summary>UIControlledApplication.DialogBoxShowing handler. Never throws.</summary>
        public void Handle(object sender, DialogBoxShowingEventArgs args)
        {
            try
            {
                if (args == null) return;
                string message = null;
                if (args is TaskDialogShowingEventArgs taskDialog) message = taskDialog.Message;
                else if (args is MessageBoxShowingEventArgs messageBox) message = messageBox.Message;
                if (message != null && message.Length > 240) message = message.Substring(0, 240);
                var record = new DialogRecord
                {
                    Id = string.IsNullOrWhiteSpace(args.DialogId) ? "(unnamed dialog)" : args.DialogId,
                    Message = message,
                    Answer = null,
                    Auto = false,
                    RequestId = McpRuntime.Queue?.Executing?.RequestId,
                    AtUtc = DocumentRegistry.Utc(DateTime.UtcNow)
                };
                lock (_gate) _last = record;
                DiagnosticsLogger.Info("dialog", "Revit showed dialog " + record.Id + (message == null ? string.Empty : ": " + message));
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("dialog", "Recording a Revit dialog failed.", ex);
            }
        }
    }
}
