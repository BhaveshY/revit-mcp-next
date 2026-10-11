using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    /// <summary>
    /// Control ops <c>dialogs</c> and <c>press</c> (SPEC §4.6.4, §8.5). Pipe thread only: Win32 + UI Automation through
    /// <see cref="DialogUia"/>, never the Revit API, never the UI thread.
    /// <list type="bullet">
    /// <item>dialogs: every open modal dialog of this Revit as {dialog:"d3", hwnd, title, text, buttons[{name,id}], dialogId?,
    /// since, ours}. Buttons that UIA cannot read within 2 s are listed as [] (title and text only).</item>
    /// <item>press {dialog?, button, confirmed?}: Cancel/Close (Abbrechen/Schließen) apply at once; any other button returns
    /// needsConfirm (rule button) unless confirmed; No/Do not save/Yes on save/sync/delete dialogs, our own dialogs and
    /// denylisted dialogs are refused with DIALOG_PROTECTED even when confirmed.</item>
    /// </list>
    /// </summary>
    internal sealed partial class ControlOperations
    {
        private static readonly object DialogHandleGate = new object();
        private static readonly Dictionary<long, string> DialogHandles = new Dictionary<long, string>();
        private static int _nextDialogHandle;

        /// <summary>Buttons that only dismiss a dialog (apply without the user's OK).</summary>
        private static readonly Regex DismissButton = new Regex("^(cancel|close|abbrechen|schlie(ß|ss)en)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Buttons that decide about work (refused on save/sync/delete dialogs).</summary>
        private static readonly Regex DecisionButton = new Regex(
            "^(no|nein|yes|ja|do ?n[o']t save|don'?t save|nicht speichern|discard|verwerfen)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static BridgeResponse Dialogs(BridgeRequest request)
        {
            List<DialogEntry> entries = OpenDialogs();
            var data = entries.Select(entry => (object)entry.Info.ToWire()).ToList();
            string summary = entries.Count == 0
                ? "no open dialogs"
                : entries.Count.ToString(CultureInfo.InvariantCulture) + " open dialog" + (entries.Count == 1 ? string.Empty : "s") + ": " +
                  string.Join("; ", entries.Select(e => DialogUia.Describe(e.Window, 6)));
            return BridgeResponse.Success(request.RequestId, data, summary);
        }

        private static BridgeResponse Press(BridgeRequest request)
        {
            string buttonArg = (Wire.GetString(request.Args, "button") ?? string.Empty).Trim();
            if (buttonArg.Length == 0)
            {
                throw OpException.InvalidArgs("button", "press needs the button text or id from ui {op:\"dialogs\"}.",
                    new Dictionary<string, object> { ["op"] = "press", ["button"] = "Cancel" });
            }
            string dialogArg = (Wire.GetString(request.Args, "dialog") ?? string.Empty).Trim();
            bool confirmed = Wire.GetBool(request.Args, "confirmed") ?? false;

            List<DialogEntry> entries = OpenDialogs();
            DialogEntry target = SelectDialog(entries, dialogArg);
            DialogInfo info = target.Info;
            DialogButtonRef button = SelectButton(target, buttonArg);

            if (DialogPolicy.IsOwn(info.DialogId, info.Title) || info.Ours)
            {
                throw Protected(info, button, "it is a Revit MCP Next dialog that only the user may answer");
            }
            if (McpRuntime.Dialogs != null && McpRuntime.Dialogs.IsDenied(info.DialogId))
            {
                throw Protected(info, button, "the dialog " + info.DialogId + " is on the deny list");
            }
            if (DecisionButton.IsMatch(button.Name) && DialogPolicy.MentionsDangerousAction(info.DialogId, info.Title, info.Text))
            {
                throw Protected(info, button, "'" + button.Name + "' on a save/sync/delete dialog could discard work");
            }

            bool dismiss = DismissButton.IsMatch(button.Name) ||
                           (button.CommandId == NativeMethods.IDCANCEL && !DecisionButton.IsMatch(button.Name)) ||
                           (button.CommandId == NativeMethods.IDCLOSE && !DecisionButton.IsMatch(button.Name));
            if (!dismiss && !confirmed)
            {
                var plan = new Dictionary<string, object>
                {
                    ["dialog"] = info.Dialog,
                    ["title"] = info.Title,
                    ["text"] = info.Text,
                    ["button"] = button.Name,
                    ["buttons"] = info.Buttons.Select(b => (object)b.ToWire()).ToList()
                };
                BridgeResponse needs = BridgeResponse.Success(request.RequestId, plan,
                    "press '" + button.Name + "' on the Revit dialog '" + info.Title + "'");
                needs.NeedsConfirm = new NeedsConfirm
                {
                    Rule = "button",
                    Plan = "press '" + button.Name + "' on the Revit dialog '" + info.Title + "'" +
                           (string.IsNullOrEmpty(info.Text) ? string.Empty : " (" + DialogPolicy.Shorten(info.Text, 160) + ")"),
                    Stamp = 0
                };
                return needs;
            }

            string via = DialogUia.Press(target.Window, button, out bool closed, out string detail);
            DiagnosticsLogger.Info("dialogs", "press '" + button.Name + "' on '" + info.Title + "' (" + info.Dialog + "): " +
                (via == null ? "not delivered" : "via " + via + (closed ? ", dialog closed" : ", dialog still open")) +
                (detail == null ? string.Empty : " [" + detail + "]"),
                new Dictionary<string, object> { ["reqId"] = request.RequestId, ["confirmed"] = confirmed });
            if (via == null)
            {
                throw new OpException(ErrorCodes.ButtonNotFound,
                    "The button '" + button.Name + "' of '" + info.Title + "' could not be pressed" + (detail == null ? "." : ": " + detail + "."),
                    new Dictionary<string, object>
                    {
                        ["dialog"] = info.Dialog,
                        ["button"] = button.Name,
                        ["buttons"] = info.Buttons.Select(b => (object)b.Name).ToList()
                    });
            }

            var data = new Dictionary<string, object>
            {
                ["pressed"] = true,
                ["via"] = via,
                ["closed"] = closed,
                ["dialog"] = info.Dialog,
                ["title"] = info.Title,
                ["button"] = button.Name
            };
            return BridgeResponse.Success(request.RequestId, data,
                "pressed '" + button.Name + "' on '" + info.Title + "'" + (closed ? " (the dialog closed)" : " (the dialog is still open)"));
        }

        private sealed class DialogEntry
        {
            public DialogWindow Window;
            public DialogInfo Info;
            public DateTime Since;
        }

        /// <summary>Enumerates and describes the open dialogs (newest first) and assigns stable d# handles.</summary>
        private static List<DialogEntry> OpenDialogs()
        {
            IntPtr main = McpRuntime.UiMonitor?.MainWindow ?? IntPtr.Zero;
            var entries = new List<DialogEntry>();
            if (main == IntPtr.Zero) return entries;
            List<DialogRecord> recent = McpRuntime.Dialogs?.Recent() ?? new List<DialogRecord>();
            var live = new HashSet<long>();
            foreach (IntPtr hwnd in DialogUia.FindDialogWindows(main))
            {
                DialogWindow window;
                try
                {
                    window = DialogUia.Describe(hwnd);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Warn("dialogs", "Could not describe dialog window " + hwnd.ToInt64().ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                    continue;
                }
                if (!NativeMethods.SafeIsWindow(hwnd)) continue;
                long key = hwnd.ToInt64();
                live.Add(key);
                DateTime since = McpRuntime.UiMonitor?.NoteSeen(hwnd) ?? DateTime.UtcNow;
                string dialogId = MatchDialogId(window, recent);
                var info = new DialogInfo
                {
                    Dialog = HandleFor(key),
                    Hwnd = key,
                    Title = window.Title,
                    Text = window.Text,
                    DialogId = dialogId,
                    Since = DocumentRegistry.Utc(since),
                    Ours = DialogPolicy.IsOwn(dialogId, window.Title),
                    Buttons = window.Buttons.Select(b => new DialogButton { Name = b.Name, Id = b.DisplayId }).ToList()
                };
                entries.Add(new DialogEntry { Window = window, Info = info, Since = since });
            }
            PruneHandles(live);
            return entries.OrderByDescending(e => e.Since).ToList();
        }

        /// <summary>The DialogId Revit reported (DialogBoxShowing) for the dialog whose text matches this window.</summary>
        private static string MatchDialogId(DialogWindow window, List<DialogRecord> recent)
        {
            string haystack = Normalize(window.Title + " " + window.Text);
            foreach (DialogRecord record in recent)
            {
                if (string.IsNullOrEmpty(record.Text) || record.Answer != null) continue;
                string needle = Normalize(record.Text);
                if (needle.Length > 48) needle = needle.Substring(0, 48);
                if (needle.Length >= 6 && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return record.DialogId;
            }
            return null;
        }

        private static string Normalize(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : Regex.Replace(text, "\\s+", " ").Trim();
        }

        private static DialogEntry SelectDialog(List<DialogEntry> entries, string dialogArg)
        {
            if (entries.Count == 0)
            {
                throw new OpException(ErrorCodes.DialogNotFound, "Revit shows no dialog right now.",
                    new Dictionary<string, object> { ["dialog"] = dialogArg, ["options"] = new List<object>() });
            }
            if (dialogArg.Length == 0)
            {
                if (entries.Count == 1) return entries[0];
                // Nested modal dialogs: only the top one is enabled, and FindDialogWindows lists enabled windows only.
                throw new OpException(ErrorCodes.DialogNotFound,
                    entries.Count.ToString(CultureInfo.InvariantCulture) + " dialogs are open; name one with dialog.",
                    new Dictionary<string, object> { ["options"] = Options(entries) });
            }

            DialogEntry match = entries.FirstOrDefault(e => string.Equals(e.Info.Dialog, dialogArg, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(e => string.Equals(e.Info.Hwnd.ToString(CultureInfo.InvariantCulture), dialogArg, StringComparison.Ordinal))
                ?? entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Info.DialogId) && string.Equals(e.Info.DialogId, dialogArg, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(e => string.Equals(e.Info.Title, dialogArg, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                List<DialogEntry> partial = entries.Where(e => e.Info.Title.IndexOf(dialogArg, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (partial.Count == 1) match = partial[0];
            }
            if (match == null)
            {
                throw new OpException(ErrorCodes.DialogNotFound, "No open Revit dialog matches '" + dialogArg + "' (it may have closed).",
                    new Dictionary<string, object> { ["dialog"] = dialogArg, ["options"] = Options(entries) });
            }
            return match;
        }

        private static DialogButtonRef SelectButton(DialogEntry entry, string buttonArg)
        {
            List<DialogButtonRef> buttons = entry.Window.Buttons;
            string wanted = DialogUia.CleanName(buttonArg).TrimEnd('.').Trim();
            DialogButtonRef match = buttons.FirstOrDefault(b => string.Equals(b.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? buttons.FirstOrDefault(b => string.Equals(b.Name.TrimEnd('.').Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                ?? buttons.FirstOrDefault(b => string.Equals(b.DisplayId, buttonArg, StringComparison.OrdinalIgnoreCase))
                ?? buttons.FirstOrDefault(b => !string.IsNullOrEmpty(b.AutomationId) && string.Equals(b.AutomationId, buttonArg, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                List<DialogButtonRef> prefix = buttons.Where(b => b.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                if (prefix.Count == 1) match = prefix[0];
            }
            if (match == null)
            {
                // English names on a German UI (and the reverse) for the common buttons.
                int? common = CommonId(wanted);
                if (common.HasValue) match = buttons.FirstOrDefault(b => b.CommandId == common.Value);
            }
            if (match == null)
            {
                string message = buttons.Count == 0
                    ? "The buttons of '" + entry.Info.Title + "' could not be read (UI Automation " + (entry.Window.UiaTimedOut ? "timed out" : "found none") + ")."
                    : "The dialog '" + entry.Info.Title + "' has no button '" + buttonArg + "'.";
                throw new OpException(ErrorCodes.ButtonNotFound, message,
                    new Dictionary<string, object>
                    {
                        ["dialog"] = entry.Info.Dialog,
                        ["button"] = buttonArg,
                        ["buttons"] = buttons.Select(b => (object)b.Name).ToList()
                    });
            }
            return match;
        }

        private static int? CommonId(string name)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "ok": return NativeMethods.IDOK;
                case "cancel": case "abbrechen": return NativeMethods.IDCANCEL;
                case "yes": case "ja": return NativeMethods.IDYES;
                case "no": case "nein": return NativeMethods.IDNO;
                case "retry": case "wiederholen": return NativeMethods.IDRETRY;
                case "close": case "schließen": case "schliessen": return NativeMethods.IDCLOSE;
                default: return null;
            }
        }

        private static OpException Protected(DialogInfo info, DialogButtonRef button, string reason)
        {
            return new OpException(ErrorCodes.DialogProtected,
                "Not pressing '" + button.Name + "' on '" + info.Title + "': " + reason + ".",
                new Dictionary<string, object>
                {
                    ["dialog"] = info.Dialog,
                    ["title"] = info.Title,
                    ["dialogId"] = info.DialogId,
                    ["button"] = button.Name,
                    ["buttons"] = info.Buttons.Select(b => (object)b.Name).ToList()
                });
        }

        private static List<object> Options(List<DialogEntry> entries)
        {
            return entries.Select(e => (object)new Dictionary<string, object>
            {
                ["value"] = e.Info.Dialog,
                ["label"] = e.Info.Title + (e.Info.Buttons.Count == 0 ? string.Empty : " [" + string.Join(", ", e.Info.Buttons.Select(b => b.Name)) + "]")
            }).ToList();
        }

        private static string HandleFor(long hwnd)
        {
            lock (DialogHandleGate)
            {
                if (DialogHandles.TryGetValue(hwnd, out string handle)) return handle;
                handle = "d" + (++_nextDialogHandle).ToString(CultureInfo.InvariantCulture);
                DialogHandles[hwnd] = handle;
                return handle;
            }
        }

        private static void PruneHandles(HashSet<long> live)
        {
            lock (DialogHandleGate)
            {
                if (DialogHandles.Count <= 32) return;
                foreach (long key in DialogHandles.Keys.Where(k => !live.Contains(k) && !NativeMethods.SafeIsWindow(new IntPtr(k))).ToList())
                {
                    DialogHandles.Remove(key);
                }
            }
        }
    }
}
