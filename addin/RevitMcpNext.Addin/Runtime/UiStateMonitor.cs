using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Samples the Revit main window state every 500 ms off the UI thread (D2 §8.5, SPEC §8.8): foreground, minimized,
    /// mainWindowEnabled, hung and the owned popup {title, class, isProgress}. NativeMethods only; never calls the Revit
    /// API and never sends a message to the UI thread (titles come from InternalGetWindowText). Changes are swapped into
    /// the document snapshot copy-on-write through <see cref="DocumentRegistry.SetUi"/>; health reads the last sample.
    /// </summary>
    internal sealed class UiStateMonitor : IDisposable
    {
        public const int TickMs = 500;
        /// <summary>While Revit stays in the foreground, lastForegroundAtUtc is refreshed at most this often (registration churn).</summary>
        private static readonly TimeSpan ForegroundRefresh = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SeenRetention = TimeSpan.FromMinutes(30);

        /// <summary>Progress-like popup titles (English and German), SPEC §8.8 rule 3.</summary>
        internal static readonly Regex ProgressTitle = new Regex("progress|fortschritt|loading|laden|export|opening|öffnen|synchron",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly object _gate = new object();
        private readonly DocumentRegistry _registry;
        private readonly Dictionary<IntPtr, DateTime> _firstSeen = new Dictionary<IntPtr, DateTime>();
        private IntPtr _mainWindow = IntPtr.Zero;
        private Timer _timer;
        private bool _disposed;
        private int _sampling;
        private long _lastSampleTicks;
        private UiHealth _health = new UiHealth();
        private bool _lastForeground;
        private DateTime _lastForegroundStampUtc = DateTime.MinValue;
        private string _lastForegroundAtUtc;
        private long _samples;
        private long _faults;

        public UiStateMonitor(DocumentRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>The Revit main window handle (captured at startup and refreshed from Execute/Idling).</summary>
        public IntPtr MainWindow
        {
            get { lock (_gate) return _mainWindow; }
        }

        /// <summary>Any thread: records the main window handle when it is a live window of this process.</summary>
        public void RefreshHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            lock (_gate)
            {
                if (handle == _mainWindow) return;
            }
            if (!NativeMethods.SafeIsWindow(handle) || NativeMethods.ProcessOf(handle) != NativeMethods.CurrentPid) return;
            lock (_gate) _mainWindow = handle;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _timer != null) return;
                _timer = new Timer(_ => Sample(), null, 0, TickMs);
            }
        }

        /// <summary>Samples now unless a sample ran in the last 100 ms (called by the pump while work is pending).</summary>
        public void Nudge()
        {
            long last = Interlocked.Read(ref _lastSampleTicks);
            if (last != 0 && (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalMilliseconds < 100) return;
            ThreadPool.QueueUserWorkItem(_ => Sample());
        }

        /// <summary>The last sampled UI health (health.ui).</summary>
        public UiHealth GetHealth()
        {
            lock (_gate) return Copy(_health);
        }

        /// <summary>When this process first showed the popup window (null when never sampled).</summary>
        public DateTime? FirstSeen(IntPtr hwnd)
        {
            lock (_gate) return _firstSeen.TryGetValue(hwnd, out DateTime at) ? at : (DateTime?)null;
        }

        /// <summary>Records a dialog window seen by another component (the dialogs control op); returns its first-seen time.</summary>
        public DateTime NoteSeen(IntPtr hwnd)
        {
            lock (_gate)
            {
                if (_firstSeen.TryGetValue(hwnd, out DateTime at)) return at;
                DateTime now = DateTime.UtcNow;
                _firstSeen[hwnd] = now;
                return now;
            }
        }

        public Dictionary<string, object> GetCounters()
        {
            lock (_gate) return new Dictionary<string, object> { ["samples"] = _samples, ["faults"] = _faults, ["mainWindow"] = _mainWindow.ToInt64() };
        }

        public void Dispose()
        {
            Timer timer;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                timer = _timer;
                _timer = null;
            }
            try { timer?.Dispose(); } catch { }
        }

        /// <summary>One sample (timer thread). Never throws; overlapping ticks are skipped.</summary>
        private void Sample()
        {
            if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                }
                IntPtr main = MainWindow;
                if (!NativeMethods.SafeIsWindow(main)) return;

                DateTime now = DateTime.UtcNow;
                Interlocked.Exchange(ref _lastSampleTicks, now.Ticks);
                bool foreground = IsForeground();
                bool minimized = SafeBool(() => NativeMethods.IsIconic(main));
                bool enabled = SafeBool(() => NativeMethods.IsWindowEnabled(main), true);
                bool hung = SafeBool(() => NativeMethods.IsHungAppWindow(main));
                PopupInfo popup = FindPopup(main, enabled);

                string lastForegroundAtUtc;
                lock (_gate)
                {
                    if (foreground && (!_lastForeground || now - _lastForegroundStampUtc >= ForegroundRefresh))
                    {
                        _lastForegroundStampUtc = now;
                        _lastForegroundAtUtc = DocumentRegistry.Utc(now);
                    }
                    _lastForeground = foreground;
                    lastForegroundAtUtc = _lastForegroundAtUtc;
                    _health = new UiHealth { MainWindowEnabled = enabled, Hung = hung, Minimized = minimized, Foreground = foreground, Popup = popup };
                    if (popup != null && popup.Hwnd != 0)
                    {
                        var key = new IntPtr(popup.Hwnd);
                        if (!_firstSeen.ContainsKey(key)) _firstSeen[key] = now;
                    }
                    PruneSeenUnsafe(now);
                    _samples++;
                }

                _registry?.SetUi(new SnapshotUi
                {
                    Foreground = foreground,
                    LastForegroundAtUtc = lastForegroundAtUtc,
                    Minimized = minimized,
                    MainWindowEnabled = enabled,
                    Hung = hung,
                    Popup = popup
                });

                MaybeCloseInfoBox(popup, now);
            }
            catch (Exception ex)
            {
                lock (_gate) _faults++;
                DiagnosticsLogger.Debug("ui", "UI state sample failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _sampling, 0);
            }
        }

        /// <summary>How long an information box must stay up during an MCP request before it is closed automatically.</summary>
        private static readonly TimeSpan InfoBoxGrace = TimeSpan.FromMilliseconds(1500);
        private static readonly Regex InfoBoxButton = new Regex("^(ok|close|schließen|schliessen)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly HashSet<long> _infoBoxTried = new HashSet<long>();
        private int _infoBoxBusy;

        /// <summary>
        /// Closes a single-button information box (only OK or Close) that Revit raised while one of our requests runs and
        /// that DialogBoxShowing never reported (plain Win32 message boxes, e.g. "saved in a newer version of Revit").
        /// One button means nothing can be discarded; it is pressed once, logged and reported on the executing request as
        /// REVIT_DIALOG_ANSWERED. Anything with more buttons, our own dialogs and progress windows are left alone.
        /// </summary>
        private void MaybeCloseInfoBox(PopupInfo popup, DateTime now)
        {
            if (popup == null || popup.Hwnd == 0 || popup.IsProgress) return;
            if (!string.Equals(popup.Class, "#32770", StringComparison.Ordinal)) return;
            RevitRequestQueue queue = McpRuntime.Queue;
            if (queue == null || !queue.IsExecuting) return;
            if (!string.IsNullOrEmpty(popup.Title) && popup.Title.IndexOf("Revit MCP Next", StringComparison.OrdinalIgnoreCase) >= 0) return;
            lock (_gate)
            {
                if (_infoBoxTried.Contains(popup.Hwnd)) return;
                if (!_firstSeen.TryGetValue(new IntPtr(popup.Hwnd), out DateTime first) || now - first < InfoBoxGrace) return;
                _infoBoxTried.Add(popup.Hwnd);
                if (_infoBoxTried.Count > 256) _infoBoxTried.Clear();
            }
            if (Interlocked.Exchange(ref _infoBoxBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DialogWindow window = DialogUia.Describe(new IntPtr(popup.Hwnd));
                    if (window == null || window.Buttons == null || window.Buttons.Count != 1) return;
                    DialogButtonRef button = window.Buttons[0];
                    bool okOrClose = InfoBoxButton.IsMatch(button.Name ?? string.Empty) || button.CommandId == 1 || button.CommandId == 8;
                    if (!okOrClose) return;
                    string via = DialogUia.Press(window, button, out bool closed, out string detail);
                    string text = "Revit dialog '" + window.Title + "'" + (string.IsNullOrEmpty(window.Text) ? string.Empty : " (" + DialogPolicy.Shorten(window.Text, 160) + ")") +
                                  " was closed with " + button.Name + " automatically";
                    DiagnosticsLogger.Info("dialog", text + " via " + via + (closed ? string.Empty : " (still open: " + detail + ")"));
                    if (!closed) return;
                    RevitRequestQueue q = McpRuntime.Queue;
                    QueuedRevitWorkItem current = q?.Current ?? q?.Deferred;
                    RequestContext ctx = current?.Context;
                    if (ctx != null) ctx.Warn(WarningCodes.RevitDialogAnswered, text);
                    else current?.AddEarlyWarning(WarningCodes.RevitDialogAnswered, text);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Debug("dialog", "Information box auto-close failed: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _infoBoxBusy, 0);
                }
            });
        }

        /// <summary>
        /// The owned popup of the main window: GetLastActivePopup first; while the main window is disabled, any enabled
        /// owned window. While the main window is enabled only dialog-class (#32770) popups count, so floating palettes
        /// (Properties, Project Browser) never show up as popups.
        /// </summary>
        private static PopupInfo FindPopup(IntPtr main, bool mainEnabled)
        {
            IntPtr popup = IntPtr.Zero;
            try
            {
                IntPtr last = NativeMethods.GetLastActivePopup(main);
                if (last != IntPtr.Zero && last != main && NativeMethods.IsWindowVisible(last)) popup = last;
            }
            catch
            {
                popup = IntPtr.Zero;
            }

            if (popup == IntPtr.Zero && !mainEnabled)
            {
                foreach (IntPtr hwnd in NativeMethods.TopLevelWindowsOfThisProcess())
                {
                    if (hwnd == main) continue;
                    if (!SafeBool(() => NativeMethods.IsWindowVisible(hwnd)) || !SafeBool(() => NativeMethods.IsWindowEnabled(hwnd))) continue;
                    if (!NativeMethods.IsOwnedBy(hwnd, main) || NativeMethods.IsToolWindow(hwnd)) continue;
                    popup = hwnd;
                    break;
                }
            }
            if (popup != IntPtr.Zero && mainEnabled && !string.Equals(NativeMethods.ClassName(popup), "#32770", StringComparison.Ordinal))
            {
                popup = IntPtr.Zero;
            }
            return popup == IntPtr.Zero ? null : Describe(popup);
        }

        internal static PopupInfo Describe(IntPtr hwnd)
        {
            string title = NativeMethods.WindowText(hwnd, 256);
            string cls = NativeMethods.ClassName(hwnd);
            return new PopupInfo
            {
                Hwnd = hwnd.ToInt64(),
                Title = title,
                Class = cls,
                IsProgress = IsProgressWindow(hwnd, title)
            };
        }

        /// <summary>A msctls_progress32 child, or a progress-like title (SPEC §8.8 rule 3).</summary>
        internal static bool IsProgressWindow(IntPtr hwnd, string title)
        {
            if (!string.IsNullOrEmpty(title) && ProgressTitle.IsMatch(title)) return true;
            foreach (IntPtr child in NativeMethods.Descendants(hwnd, 256))
            {
                if (string.Equals(NativeMethods.ClassName(child), "msctls_progress32", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The root owner of the foreground window belongs to this process.</summary>
        private static bool IsForeground()
        {
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                IntPtr root = NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOTOWNER);
                uint pid = NativeMethods.ProcessOf(root != IntPtr.Zero ? root : fg);
                return pid != 0 && pid == NativeMethods.CurrentPid;
            }
            catch
            {
                return false;
            }
        }

        private void PruneSeenUnsafe(DateTime now)
        {
            if (_firstSeen.Count < 32) return;
            var stale = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, DateTime> pair in _firstSeen)
            {
                if (now - pair.Value > SeenRetention || !NativeMethods.SafeIsWindow(pair.Key)) stale.Add(pair.Key);
            }
            foreach (IntPtr hwnd in stale) _firstSeen.Remove(hwnd);
        }

        private static UiHealth Copy(UiHealth health)
        {
            if (health == null) return new UiHealth();
            return new UiHealth
            {
                MainWindowEnabled = health.MainWindowEnabled,
                Hung = health.Hung,
                Minimized = health.Minimized,
                Foreground = health.Foreground,
                Popup = health.Popup
            };
        }

        private static bool SafeBool(Func<bool> read, bool fallback = false)
        {
            try
            {
                return read();
            }
            catch
            {
                return fallback;
            }
        }
    }
}
