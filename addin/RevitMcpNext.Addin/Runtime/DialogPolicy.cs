using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI.Events;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The dialog auto-responder (SPEC §8.5, D2 §6). It runs in UIControlledApplication.DialogBoxShowing on the UI thread
    /// and decides, in this order:
    /// <list type="number">
    /// <item>Never answer: ids starting <c>RevitMcpNext_</c> (our own dialogs, e.g. the code-execution consent), the built-in
    /// denylist, <c>dialogPolicy.deny</c>, and any dialog whose id or text mentions save/sync/delete (English and German).
    /// Those always stay for the user (invariant 9: nothing presses a button that can discard user work).</item>
    /// <item>Tier A (one of our requests or job steps is executing on the UI thread, so the dialog is a side effect of our
    /// work): allowlisted ids get their fixed answer; any other dialog gets its safe dismiss answer (Cancel, or OK for an
    /// OK-only message box; Yes/No-only message boxes are never answered). The executing request gets a
    /// REVIT_DIALOG_ANSWERED warning.</item>
    /// <item>Tier B (nothing of ours executes but requests are queued behind the dialog): allowlisted ids only; the oldest
    /// pending request gets the warning.</item>
    /// <item>Otherwise (Revit is idle for us): the dialog belongs to the user; it is only recorded.</item>
    /// </list>
    /// <c>dialogPolicy.autoRespond:false</c> turns answering off; <c>dialogPolicy.extra</c> adds allowlist answers (it can
    /// never add our ids or denylisted ids). Every dialog is recorded in health.lastDialog and the log, and the last few are
    /// kept so the control op <c>dialogs</c> can attach a DialogId to a window (V20).
    /// </summary>
    internal sealed class DialogPolicy
    {
        /// <summary>Prefix of dialog ids the add-in itself shows; never answered, never pressed.</summary>
        public const string OwnPrefix = "RevitMcpNext_";

        /// <summary>Keywords of dialogs whose answers can discard work (ids, titles and texts; English and German).</summary>
        internal static readonly Regex DangerousText = new Regex(
            "save|speicher|synchron|sync|delete|l(ö|oe)sch|discard|verwerf|relinquish|freigeb",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Titles of our own dialogs (the code-execution consent uses a RevitMcpNext_ id as well).</summary>
        internal static readonly Regex OwnTitle = new Regex(
            "revit[ _-]?mcp[ _-]?next|code execution|codeausf(ü|ue)hrung|code-ausf(ü|ue)hrung",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Built-in allowlist: dialog id → answer. Only dialogs whose answer keeps every user decision intact (continue
        /// opening, keep destination types). Command links are 1001 + index.
        /// </summary>
        private static readonly Dictionary<string, int> BuiltInAnswers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // "Ignore and continue opening the project" (links stay unresolved; Manage Links can fix them later).
            ["TaskDialog_Unresolved_References"] = 1002,
            // "Continue working with the file" (third-party updaters missing).
            ["TaskDialog_Missing_Third_Party_Updaters"] = 1001,
            ["TaskDialog_Missing_Third_Party_Updater"] = 1001,
            // Duplicate types on paste/copy: OK keeps the destination types.
            ["Dialog_Revit_PasteSimilarSymbolsPaste"] = 1
        };

        /// <summary>Built-in denylist: dialogs that ask about saving, synchronizing, relinquishing or discarding work.</summary>
        private static readonly HashSet<string> BuiltInDeny = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TaskDialog_Changes_Not_Saved",
            "TaskDialog_Save_File",
            "TaskDialog_Save_Changes",
            "TaskDialog_Local_Changes_Not_Synchronized_With_Central",
            "TaskDialog_Close_Project_Without_Saving",
            "TaskDialog_Save_As_Central",
            "TaskDialog_Editable_Elements",
            "TaskDialog_Relinquish",
            "Dialog_Revit_SaveChanges",
            "Dialog_Revit_SaveAs"
        };

        private const int RecentCapacity = 16;
        private const int MaxText = 240;

        private readonly object _gate = new object();
        private readonly Func<DialogPolicySettings> _settings;
        private readonly LinkedList<DialogRecord> _recent = new LinkedList<DialogRecord>();
        private DialogRecord _last;
        private long _seen;
        private long _answered;

        public DialogPolicy(Func<DialogPolicySettings> settings)
        {
            _settings = settings ?? (() => new DialogPolicySettings());
        }

        /// <summary>The last dialog Revit showed (health.lastDialog), or null.</summary>
        public DialogRecord LastDialog
        {
            get { lock (_gate) return _last; }
        }

        /// <summary>The most recent dialogs (newest first) for matching windows to DialogIds.</summary>
        public List<DialogRecord> Recent()
        {
            lock (_gate) return _recent.ToList();
        }

        public Dictionary<string, object> GetCounters()
        {
            lock (_gate) return new Dictionary<string, object> { ["seen"] = _seen, ["answered"] = _answered };
        }

        /// <summary>True for our own dialogs (id prefix or title); they are never answered and never pressed.</summary>
        public static bool IsOwn(string dialogId, string title)
        {
            if (!string.IsNullOrEmpty(dialogId) && dialogId.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(title) && OwnTitle.IsMatch(title);
        }

        /// <summary>True when the id is on the built-in denylist or dialogPolicy.deny.</summary>
        public bool IsDenied(string dialogId)
        {
            if (string.IsNullOrWhiteSpace(dialogId)) return false;
            if (BuiltInDeny.Contains(dialogId)) return true;
            DialogPolicySettings settings = SafeSettings();
            return settings.Deny != null && settings.Deny.Any(d => string.Equals(d, dialogId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True when the id, title or text mentions saving, syncing or deleting (SPEC §8.5 press protection).</summary>
        public static bool MentionsDangerousAction(string dialogId, string title, string text)
        {
            return (!string.IsNullOrEmpty(dialogId) && DangerousText.IsMatch(dialogId)) ||
                   (!string.IsNullOrEmpty(title) && DangerousText.IsMatch(title)) ||
                   (!string.IsNullOrEmpty(text) && DangerousText.IsMatch(text));
        }

        /// <summary>UIControlledApplication.DialogBoxShowing handler (UI thread). Never throws.</summary>
        public void Handle(object sender, DialogBoxShowingEventArgs args)
        {
            try
            {
                if (args == null) return;
                string dialogId = SafeId(args);
                string message = null;
                int messageBoxType = -1;
                string family = "dialog";
                if (args is TaskDialogShowingEventArgs taskDialog)
                {
                    family = "taskDialog";
                    message = SafeString(() => taskDialog.Message);
                }
                else if (args is MessageBoxShowingEventArgs messageBox)
                {
                    family = "messageBox";
                    message = SafeString(() => messageBox.Message);
                    try { messageBoxType = messageBox.DialogType; } catch { messageBoxType = -1; }
                }
                if (message != null && message.Length > MaxText) message = message.Substring(0, MaxText);

                RevitRequestQueue queue = McpRuntime.Queue;
                ExecutingInfo executing = queue?.Executing;
                QueuedRevitWorkItem deferred = queue?.Deferred;
                bool ours = executing != null || deferred != null;
                QueuedRevitWorkItem oldestPending = ours ? null : queue?.OldestPending;

                var record = new DialogRecord
                {
                    DialogId = string.IsNullOrWhiteSpace(dialogId) ? "(unnamed dialog)" : dialogId,
                    Text = message,
                    Answer = null,
                    Auto = false,
                    RequestId = executing?.RequestId ?? deferred?.RequestId ?? oldestPending?.RequestId,
                    Ours = ours,
                    AtUtc = DocumentRegistry.Utc(DateTime.UtcNow)
                };

                int answer = Decide(dialogId, message, family, messageBoxType, ours, oldestPending != null, out string why);
                if (answer > 0)
                {
                    bool accepted;
                    try
                    {
                        accepted = args.OverrideResult(answer);
                    }
                    catch (Exception ex)
                    {
                        accepted = false;
                        DiagnosticsLogger.Warn("dialog", "OverrideResult(" + answer.ToString(CultureInfo.InvariantCulture) + ") failed for " + record.DialogId + ": " + ex.Message);
                    }

                    if (accepted)
                    {
                        record.Answer = AnswerName(answer);
                        record.Auto = true;
                        lock (_gate) _answered++;
                        Attribute(record, ours, oldestPending);
                    }
                    else
                    {
                        record.OverrideRejected = true;
                        why = why + "; Revit rejected answer " + AnswerName(answer);
                    }
                }

                Remember(record);
                DiagnosticsLogger.Info("dialog", "Revit showed " + family + " " + record.DialogId +
                    (record.Answer != null ? " (answered " + record.Answer + ")" : " (not answered: " + why + ")") +
                    (message == null ? string.Empty : ": " + message),
                    new Dictionary<string, object>
                    {
                        ["dialogId"] = record.DialogId,
                        ["answer"] = record.Answer,
                        ["ours"] = ours,
                        ["reqId"] = record.RequestId,
                        ["why"] = why
                    });
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("dialog", "Handling a Revit dialog failed.", ex);
            }
        }

        /// <summary>The answer for a dialog (0 = leave it for the user) and the reason.</summary>
        private int Decide(string dialogId, string message, string family, int messageBoxType, bool executing, bool pending, out string why)
        {
            DialogPolicySettings settings = SafeSettings();
            if (!settings.AutoRespond)
            {
                why = "dialogPolicy.autoRespond is off";
                return 0;
            }
            if (IsOwn(dialogId, null))
            {
                why = "our own dialog";
                return 0;
            }
            if (IsDenied(dialogId))
            {
                why = "denylisted";
                return 0;
            }
            if (MentionsDangerousAction(dialogId, null, message))
            {
                why = "it may discard work (save/sync/delete)";
                return 0;
            }

            int allow = AllowlistAnswer(dialogId, settings);
            if (allow > 0 && (executing || pending))
            {
                why = executing ? "allowlisted (tier A)" : "allowlisted (tier B)";
                return allow;
            }
            if (!executing)
            {
                why = pending
                    ? "not allowlisted and nothing of ours executes (tier B answers allowlisted ids only)"
                    : "Revit is idle for MCP; the dialog belongs to the user";
                return 0;
            }

            int dismiss = DismissAnswer(family, messageBoxType);
            why = dismiss > 0 ? "dismissed during our request (tier A)" : "no neutral answer (Yes/No message box)";
            return dismiss;
        }

        private static int AllowlistAnswer(string dialogId, DialogPolicySettings settings)
        {
            if (string.IsNullOrWhiteSpace(dialogId)) return 0;
            if (settings.Extra != null)
            {
                foreach (KeyValuePair<string, int> pair in settings.Extra)
                {
                    if (string.Equals(pair.Key, dialogId, StringComparison.OrdinalIgnoreCase) && pair.Value > 0) return pair.Value;
                }
            }
            return BuiltInAnswers.TryGetValue(dialogId, out int answer) ? answer : 0;
        }

        /// <summary>
        /// The safe dismiss answer: Cancel for task dialogs and Revit dialogs, OK for OK-only message boxes, Cancel/Abort
        /// for message boxes that have it; 0 for Yes/No message boxes (both answers are decisions).
        /// </summary>
        internal static int DismissAnswer(string family, int messageBoxType)
        {
            if (family != "messageBox") return NativeMethods.IDCANCEL;
            switch (messageBoxType < 0 ? -1 : messageBoxType & 0xF)
            {
                case 0: return NativeMethods.IDOK;        // MB_OK
                case 1: return NativeMethods.IDCANCEL;    // MB_OKCANCEL
                case 2: return NativeMethods.IDABORT;     // MB_ABORTRETRYIGNORE
                case 3: return NativeMethods.IDCANCEL;    // MB_YESNOCANCEL
                case 4: return 0;                         // MB_YESNO: no neutral answer
                case 5: return NativeMethods.IDCANCEL;    // MB_RETRYCANCEL
                case 6: return NativeMethods.IDCANCEL;    // MB_CANCELTRYCONTINUE
                default: return NativeMethods.IDCANCEL;
            }
        }

        internal static string AnswerName(int answer)
        {
            switch (answer)
            {
                case 1: return "OK";
                case 2: return "Cancel";
                case 3: return "Abort";
                case 4: return "Retry";
                case 5: return "Ignore";
                case 6: return "Yes";
                case 7: return "No";
                case 8: return "Close";
                default:
                    return answer > 1000 && answer < 1100
                        ? "CommandLink" + (answer - 1000).ToString(CultureInfo.InvariantCulture)
                        : answer.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Adds REVIT_DIALOG_ANSWERED to the executing request (tier A) or the oldest pending one (tier B).</summary>
        private static void Attribute(DialogRecord record, bool executing, QueuedRevitWorkItem oldestPending)
        {
            string text = "Revit dialog " + record.DialogId + (string.IsNullOrEmpty(record.Text) ? string.Empty : " (" + Shorten(record.Text, 120) + ")") +
                          " was answered " + record.Answer + " automatically";
            try
            {
                if (executing)
                {
                    RevitRequestQueue queue = McpRuntime.Queue;
                    RequestContext ctx = RequestContext.Current ?? queue?.Current?.Context ?? queue?.Deferred?.Context;
                    if (ctx != null)
                    {
                        ctx.Warn(WarningCodes.RevitDialogAnswered, text);
                        return;
                    }
                    QueuedRevitWorkItem current = queue?.Current ?? queue?.Deferred;
                    if (current != null)
                    {
                        current.AddEarlyWarning(WarningCodes.RevitDialogAnswered, text);
                        return;
                    }
                }
                oldestPending?.AddEarlyWarning(WarningCodes.RevitDialogAnswered, text);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("dialog", "Could not attach REVIT_DIALOG_ANSWERED: " + ex.Message);
            }
        }

        private void Remember(DialogRecord record)
        {
            lock (_gate)
            {
                _last = record;
                _seen++;
                _recent.AddFirst(record);
                while (_recent.Count > RecentCapacity) _recent.RemoveLast();
            }
        }

        private DialogPolicySettings SafeSettings()
        {
            try { return _settings() ?? new DialogPolicySettings(); } catch { return new DialogPolicySettings(); }
        }

        private static string SafeId(DialogBoxShowingEventArgs args)
        {
            try { return args.DialogId ?? string.Empty; } catch { return string.Empty; }
        }

        private static string SafeString(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }

        internal static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
        }
    }
}
