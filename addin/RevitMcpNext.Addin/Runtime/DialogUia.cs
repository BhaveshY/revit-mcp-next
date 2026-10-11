using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin
{
    /// <summary>One button of an open dialog.</summary>
    internal sealed class DialogButtonRef
    {
        /// <summary>Display name with '&amp;' removed.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Win32/TaskDialog command id when known (1 OK, 2 Cancel, 6 Yes, 7 No, 8 Close, 1001+ command links).</summary>
        public int? CommandId { get; set; }
        public string AutomationId { get; set; }
        /// <summary>The Win32 button window (classic dialogs); zero for TaskDialog (DirectUI) buttons.</summary>
        public IntPtr Hwnd { get; set; }
        public bool FromUia { get; set; }

        /// <summary>The id shown to callers: the command id, else the AutomationId, else b&lt;index&gt;.</summary>
        public string DisplayId { get; set; } = string.Empty;
    }

    /// <summary>An open modal dialog window owned by the Revit main window.</summary>
    internal sealed class DialogWindow
    {
        public IntPtr Hwnd { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Class { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public bool IsTaskDialog { get; set; }
        public bool UiaTimedOut { get; set; }
        public List<DialogButtonRef> Buttons { get; set; } = new List<DialogButtonRef>();
    }

    /// <summary>
    /// Dialog discovery and button presses for the control ops <c>dialogs</c> and <c>press</c> (SPEC §8.5). Runs on pipe
    /// threads and never calls the Revit API: windows are found with Win32 (no messages to the UI thread), buttons and
    /// text are read through UI Automation on a dedicated STA thread with a 2 s timeout (TaskDialog DirectUI buttons are
    /// only visible to UIA), with a Win32 child-window fallback for classic dialogs. Press mechanisms in order: UIA
    /// InvokePattern, TDM_CLICK_BUTTON (task dialogs), WM_COMMAND(id), BM_CLICK on the button window.
    /// </summary>
    internal static class DialogUia
    {
        public const int UiaTimeoutMs = 2000;
        private const int MaxAbandonedStaThreads = 4;
        private const int MaxTextChars = 600;
        private static int _abandoned;

        private static readonly Regex Ampersand = new Regex("&(?!&)", RegexOptions.CultureInvariant);

        /// <summary>Common command ids by button name (English and German).</summary>
        private static readonly Dictionary<string, int> CommonIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"] = NativeMethods.IDOK,
            ["cancel"] = NativeMethods.IDCANCEL,
            ["abbrechen"] = NativeMethods.IDCANCEL,
            ["abort"] = NativeMethods.IDABORT,
            ["retry"] = NativeMethods.IDRETRY,
            ["wiederholen"] = NativeMethods.IDRETRY,
            ["ignore"] = NativeMethods.IDIGNORE,
            ["ignorieren"] = NativeMethods.IDIGNORE,
            ["yes"] = NativeMethods.IDYES,
            ["ja"] = NativeMethods.IDYES,
            ["no"] = NativeMethods.IDNO,
            ["nein"] = NativeMethods.IDNO,
            ["close"] = NativeMethods.IDCLOSE,
            ["schließen"] = NativeMethods.IDCLOSE,
            ["schliessen"] = NativeMethods.IDCLOSE
        };

        // ------------------------------------------------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The open dialogs of this Revit: enabled, visible top-level windows owned by the main window that are dialogs
        /// (class #32770, which includes task dialogs) or, while the main window is disabled, any other non-tool owned
        /// window (WPF modal dialogs). Floating palettes (tool windows) are never listed.
        /// </summary>
        public static List<IntPtr> FindDialogWindows(IntPtr main)
        {
            var list = new List<IntPtr>();
            if (!NativeMethods.SafeIsWindow(main)) return list;
            bool mainEnabled = SafeBool(() => NativeMethods.IsWindowEnabled(main), true);
            foreach (IntPtr hwnd in NativeMethods.TopLevelWindowsOfThisProcess())
            {
                if (hwnd == main) continue;
                if (!SafeBool(() => NativeMethods.IsWindowVisible(hwnd)) || !SafeBool(() => NativeMethods.IsWindowEnabled(hwnd))) continue;
                if (!NativeMethods.IsOwnedBy(hwnd, main)) continue;
                string cls = NativeMethods.ClassName(hwnd);
                bool dialogClass = string.Equals(cls, "#32770", StringComparison.Ordinal);
                if (!dialogClass && (mainEnabled || NativeMethods.IsToolWindow(hwnd))) continue;
                list.Add(hwnd);
            }
            return list;
        }

        /// <summary>Describes a dialog window: title, text and buttons (UIA with a 2 s timeout, Win32 fallback).</summary>
        public static DialogWindow Describe(IntPtr hwnd)
        {
            var window = new DialogWindow
            {
                Hwnd = hwnd,
                Title = NativeMethods.WindowText(hwnd, 256),
                Class = NativeMethods.ClassName(hwnd)
            };
            List<IntPtr> children = NativeMethods.Descendants(hwnd, 256);
            window.IsTaskDialog = children.Any(child => string.Equals(NativeMethods.ClassName(child), "DirectUIHWND", StringComparison.Ordinal));

            // Win32 view first (no cross-thread messages): push buttons and static texts of classic dialogs.
            var win32Buttons = new List<DialogButtonRef>();
            var texts = new List<string>();
            foreach (IntPtr child in children)
            {
                if (!SafeBool(() => NativeMethods.IsWindowVisible(child))) continue;
                string cls = NativeMethods.ClassName(child);
                if (string.Equals(cls, "Button", StringComparison.OrdinalIgnoreCase) && NativeMethods.IsPushButton(child))
                {
                    string name = CleanName(NativeMethods.WindowText(child, 128));
                    if (name.Length == 0) continue;
                    int id = 0;
                    try { id = NativeMethods.GetDlgCtrlID(child); } catch { id = 0; }
                    win32Buttons.Add(new DialogButtonRef { Name = name, CommandId = id > 0 ? id : (int?)null, Hwnd = child });
                }
                else if (string.Equals(cls, "Static", StringComparison.OrdinalIgnoreCase))
                {
                    string text = NativeMethods.WindowText(child, 512).Trim();
                    if (text.Length > 0) texts.Add(text);
                }
            }

            UiaView uia = ReadUia(hwnd, out bool timedOut);
            window.UiaTimedOut = timedOut;
            if (uia != null)
            {
                foreach (UiaButton button in uia.Buttons)
                {
                    DialogButtonRef match = win32Buttons.FirstOrDefault(b => string.Equals(b.Name, button.Name, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        match.AutomationId = button.AutomationId;
                        match.FromUia = true;
                        continue;
                    }
                    window.Buttons.Add(new DialogButtonRef
                    {
                        Name = button.Name,
                        AutomationId = button.AutomationId,
                        CommandId = ParseCommandId(button.AutomationId),
                        FromUia = true
                    });
                }
                if (uia.Texts.Count > 0) texts = uia.Texts;
            }
            window.Buttons.InsertRange(0, win32Buttons);

            // Task dialog buttons without a numeric AutomationId: common names map to their ids, command links to 1001+.
            int commandLink = 1001;
            foreach (DialogButtonRef button in window.Buttons)
            {
                if (button.CommandId == null && CommonIds.TryGetValue(button.Name, out int common)) button.CommandId = common;
            }
            foreach (DialogButtonRef button in window.Buttons.Where(b => b.CommandId == null && window.IsTaskDialog))
            {
                button.CommandId = commandLink++;
            }
            for (int index = 0; index < window.Buttons.Count; index++)
            {
                DialogButtonRef button = window.Buttons[index];
                button.DisplayId = button.CommandId.HasValue
                    ? button.CommandId.Value.ToString(CultureInfo.InvariantCulture)
                    : !string.IsNullOrEmpty(button.AutomationId) ? button.AutomationId : "b" + (index + 1).ToString(CultureInfo.InvariantCulture);
            }

            string joined = string.Join(" ", texts.Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, window.Title, StringComparison.Ordinal)).Distinct());
            window.Text = joined.Length > MaxTextChars ? joined.Substring(0, MaxTextChars - 3) + "..." : joined;
            return window;
        }

        /// <summary>Button names: '&amp;' accelerators removed, whitespace collapsed, trailing "..." kept.</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string cleaned = Ampersand.Replace(name, string.Empty).Replace("&&", "&");
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            return cleaned;
        }

        private static int? ParseCommandId(string automationId)
        {
            if (string.IsNullOrWhiteSpace(automationId)) return null;
            Match digits = Regex.Match(automationId, "(\\d+)$");
            if (!digits.Success) return null;
            if (!int.TryParse(digits.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return null;
            // Bare numbers are command ids; CommandButton_N / CommandLink_N style ids keep N only when it looks like one.
            return id > 0 && id < 65536 ? id : (int?)null;
        }

        // ------------------------------------------------------------------------------------------------------------
        // Press
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Presses a button. Returns the mechanism used ("uia" | "tdm" | "wm_command" | "bm_click") or null when nothing could
        /// be delivered. <paramref name="closed"/> reports whether the dialog window went away within 1.5 s.
        /// </summary>
        public static string Press(DialogWindow window, DialogButtonRef button, out bool closed, out string detail)
        {
            closed = false;
            detail = null;
            IntPtr hwnd = window.Hwnd;

            // 1. UI Automation InvokePattern.
            if (button.FromUia || window.IsTaskDialog)
            {
                bool invoked = RunSta(() => InvokeUia(hwnd, button), UiaTimeoutMs, out bool timedOut, out Exception error);
                if (invoked)
                {
                    closed = WaitClosed(hwnd, 1500);
                    return "uia";
                }
                if (timedOut && WaitClosed(hwnd, 500))
                {
                    closed = true;
                    return "uia";
                }
                detail = timedOut ? "UI Automation timed out" : "UI Automation could not invoke the button" + (error == null ? string.Empty : ": " + error.Message);
            }

            // 2. Task dialogs: TDM_CLICK_BUTTON (SendMessageTimeout so a busy UI thread never blocks the pipe thread).
            if (window.IsTaskDialog && button.CommandId.HasValue)
            {
                if (NativeMethods.SendWithTimeout(hwnd, NativeMethods.TDM_CLICK_BUTTON, new IntPtr(button.CommandId.Value), IntPtr.Zero, 2000))
                {
                    closed = WaitClosed(hwnd, 1500);
                    return "tdm";
                }
                if (WaitClosed(hwnd, 300))
                {
                    closed = true;
                    return "tdm";
                }
                detail = (detail == null ? string.Empty : detail + "; ") + "TDM_CLICK_BUTTON was not processed";
            }

            // 3. Classic dialogs: WM_COMMAND(id, BN_CLICKED) posted to the dialog.
            if (!window.IsTaskDialog && button.CommandId.HasValue)
            {
                if (NativeMethods.Post(hwnd, NativeMethods.WM_COMMAND, NativeMethods.MakeWParam(button.CommandId.Value, NativeMethods.BN_CLICKED), button.Hwnd))
                {
                    closed = WaitClosed(hwnd, 1500);
                    return "wm_command";
                }
                detail = (detail == null ? string.Empty : detail + "; ") + "WM_COMMAND could not be posted";
            }

            // 4. BM_CLICK on the button window.
            if (button.Hwnd != IntPtr.Zero && NativeMethods.SafeIsWindow(button.Hwnd))
            {
                if (NativeMethods.Post(button.Hwnd, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero))
                {
                    closed = WaitClosed(hwnd, 1500);
                    return "bm_click";
                }
                detail = (detail == null ? string.Empty : detail + "; ") + "BM_CLICK could not be posted";
            }
            return null;
        }

        private static bool InvokeUia(IntPtr hwnd, DialogButtonRef target)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;
            foreach (AutomationElement element in Buttons(root))
            {
                string name = CleanName(SafeName(element));
                string automationId = SafeAutomationId(element);
                bool sameId = !string.IsNullOrEmpty(target.AutomationId) && string.Equals(automationId, target.AutomationId, StringComparison.Ordinal);
                bool sameName = string.Equals(name, target.Name, StringComparison.OrdinalIgnoreCase);
                if (!sameId && !sameName) continue;
                if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern) || !(pattern is InvokePattern invoke)) continue;
                invoke.Invoke();
                return true;
            }
            return false;
        }

        private static bool WaitClosed(IntPtr hwnd, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                if (!NativeMethods.SafeIsWindow(hwnd) || !SafeBool(() => NativeMethods.IsWindowVisible(hwnd))) return true;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(50);
            }
        }

        // ------------------------------------------------------------------------------------------------------------
        // UI Automation reads (STA thread, timeout)
        // ------------------------------------------------------------------------------------------------------------

        private sealed class UiaButton
        {
            public string Name;
            public string AutomationId;
        }

        private sealed class UiaView
        {
            public readonly List<UiaButton> Buttons = new List<UiaButton>();
            public List<string> Texts = new List<string>();
        }

        private static UiaView ReadUia(IntPtr hwnd, out bool timedOut)
        {
            UiaView view = RunSta(() =>
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return null;
                var result = new UiaView();
                foreach (AutomationElement element in Buttons(root))
                {
                    string name = CleanName(SafeName(element));
                    if (name.Length == 0) continue;
                    result.Buttons.Add(new UiaButton { Name = name, AutomationId = SafeAutomationId(element) });
                }
                var texts = new List<string>();
                AutomationElementCollection textElements = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                int chars = 0;
                foreach (AutomationElement element in textElements)
                {
                    string text = SafeName(element).Trim();
                    if (text.Length == 0) continue;
                    texts.Add(text);
                    chars += text.Length;
                    if (chars > MaxTextChars) break;
                }
                result.Texts = texts;
                return result;
            }, UiaTimeoutMs, out timedOut, out Exception error);
            if (error != null) DiagnosticsLogger.Debug("dialogs", "UI Automation read failed: " + error.Message);
            return view;
        }

        /// <summary>Button elements of a dialog, excluding the title-bar buttons (minimize, maximize, close glyphs).</summary>
        private static IEnumerable<AutomationElement> Buttons(AutomationElement root)
        {
            AutomationElementCollection all = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            TreeWalker walker = TreeWalker.ControlViewWalker;
            foreach (AutomationElement element in all)
            {
                bool titleBar = false;
                try
                {
                    AutomationElement parent = walker.GetParent(element);
                    titleBar = parent != null && parent.Current.ControlType == ControlType.TitleBar;
                }
                catch
                {
                    titleBar = false;
                }
                if (titleBar) continue;
                bool enabled = true;
                try { enabled = element.Current.IsEnabled; } catch { enabled = true; }
                if (!enabled) continue;
                yield return element;
            }
        }

        private static string SafeName(AutomationElement element)
        {
            try { return element.Current.Name ?? string.Empty; } catch { return string.Empty; }
        }

        private static string SafeAutomationId(AutomationElement element)
        {
            try { return element.Current.AutomationId ?? string.Empty; } catch { return string.Empty; }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a fresh STA thread and waits up to <paramref name="timeoutMs"/>. A timed-out
        /// thread is abandoned (background); at most <see cref="MaxAbandonedStaThreads"/> may hang at once, after which
        /// UIA is skipped (timedOut = true) until they finish.
        /// </summary>
        private static T RunSta<T>(Func<T> work, int timeoutMs, out bool timedOut, out Exception error)
        {
            timedOut = false;
            error = null;
            if (Volatile.Read(ref _abandoned) >= MaxAbandonedStaThreads)
            {
                timedOut = true;
                return default(T);
            }
            T result = default(T);
            Exception failure = null;
            // 0 running, 1 abandoned by the caller, 2 finished.
            int state = 0;
            var thread = new Thread(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    if (Interlocked.Exchange(ref state, 2) == 1) Interlocked.Decrement(ref _abandoned);
                }
            })
            {
                IsBackground = true,
                Name = "RevitMcpNext UIA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(timeoutMs))
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) == 0)
                {
                    Interlocked.Increment(ref _abandoned);
                    timedOut = true;
                    return default(T);
                }
                thread.Join(250);
            }
            error = failure;
            return result;
        }

        private static bool SafeBool(Func<bool> read, bool fallback = false)
        {
            try { return read(); } catch { return fallback; }
        }

        internal static string Describe(DialogWindow window, int maxButtons = 12)
        {
            var builder = new StringBuilder();
            builder.Append('\'').Append(window.Title).Append('\'');
            if (window.Buttons.Count > 0)
            {
                builder.Append(" [");
                builder.Append(string.Join(", ", window.Buttons.Take(maxButtons).Select(b => b.Name)));
                builder.Append(']');
            }
            return builder.ToString();
        }
    }
}
