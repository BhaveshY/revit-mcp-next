using System;
using System.Globalization;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin.Code
{
    /// <summary>
    /// Per-Revit-session consent for run_csharp (SPEC §11.2 with lead override 5): the add-in-owned TaskDialog
    /// "Revit MCP Next: allow AI code execution?" (Id RevitMcpNext_CodeConsent; DialogPolicy and ui press never answer
    /// RevitMcpNext_* ids) is shown only when codeExecution.consentPrompt is true. "Allow for this Revit session" consents
    /// until Revit exits; "Block" declines and suppresses the prompt for 10 minutes; closing the dialog declines once.
    /// Isolated e2e homes may pre-approve (codeExecution.e2ePreapproved, SPEC §13.4).
    /// </summary>
    internal static class CodeConsent
    {
        public const string DialogId = "RevitMcpNext_CodeConsent";
        public const string DialogTitle = "Revit MCP Next: allow AI code execution?";
        public static readonly TimeSpan BlockSuppression = TimeSpan.FromMinutes(10);

        private static readonly object Gate = new object();
        private static bool _consented;
        private static DateTime _blockedUntilUtc = DateTime.MinValue;

        /// <summary>granted | preapproved | not_required | pending | blocked.</summary>
        public static string State(McpSettings settings)
        {
            lock (Gate)
            {
                if (!settings.CodeExecution.ConsentPrompt) return "not_required";
                if (McpRuntime.CodeExecutionPreapproved) return "preapproved";
                if (_consented) return "granted";
                if (DateTime.UtcNow < _blockedUntilUtc) return "blocked";
                return "pending";
            }
        }

        /// <summary>Seconds until the Block suppression ends (0 when not blocked).</summary>
        public static int BlockedForSeconds
        {
            get
            {
                lock (Gate)
                {
                    double seconds = (_blockedUntilUtc - DateTime.UtcNow).TotalSeconds;
                    return seconds > 0 ? (int)Math.Ceiling(seconds) : 0;
                }
            }
        }

        /// <summary>Publishes the enablement/consent pair to the DocSnapshot (status shows it).</summary>
        public static void Publish(McpSettings settings)
        {
            try
            {
                string state = State(settings);
                bool consented = state == "granted" || state == "preapproved" || state == "not_required";
                McpRuntime.Registry?.SetCodeExecution(settings.EnableCodeExecution, settings.EnableCodeExecution && consented, McpRuntime.Settings?.FileMtimeUtc);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("code", "Publishing the code-execution state failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Shows the consent TaskDialog (UI thread, API context; modal). Returns true when the user allowed execution for
        /// this Revit session.
        /// </summary>
        public static bool Ask(string client, string codeSha, string code, string docTitle, string mode)
        {
            var dialog = new TaskDialog(DialogTitle)
            {
                Id = DialogId,
                TitleAutoPrefix = false,
                MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                MainInstruction = "An AI client wants to run C# code in this Revit session.",
                MainContent =
                    "Client: " + (string.IsNullOrWhiteSpace(client) ? "unknown" : client) + "\n" +
                    "Document: " + (docTitle ?? "?") + "\n" +
                    "Mode: " + mode + "\n" +
                    "Code sha256: " + (codeSha ?? "?").Substring(0, Math.Min(16, (codeSha ?? "?").Length)) + "...\n\n" +
                    ScriptSource.Head(code, 12),
                FooterText = "Code runs with your Windows rights inside Revit. Every run is logged in code-exec-audit.jsonl. " +
                             "Dry runs and reads are always rolled back; a commit still needs your OK in the chat.",
                AllowCancellation = true,
                CommonButtons = TaskDialogCommonButtons.None
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Allow for this Revit session", "Allow AI code execution until Revit closes.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Block", "Refuse this code (and do not ask again for 10 minutes).");

            TaskDialogResult answer = dialog.Show();
            lock (Gate)
            {
                switch (answer)
                {
                    case TaskDialogResult.CommandLink1:
                        _consented = true;
                        _blockedUntilUtc = DateTime.MinValue;
                        break;
                    case TaskDialogResult.CommandLink2:
                        _blockedUntilUtc = DateTime.UtcNow + BlockSuppression;
                        break;
                }
            }
            DiagnosticsLogger.Info("code", "Code-execution consent answered: " + answer.ToString() + " (client " + client + ", sha " + codeSha + ").");
            return answer == TaskDialogResult.CommandLink1;
        }

        public static string Describe(string state)
        {
            switch (state)
            {
                case "blocked": return "blocked for " + BlockedForSeconds.ToString(CultureInfo.InvariantCulture) + " s";
                default: return state;
            }
        }
    }
}
