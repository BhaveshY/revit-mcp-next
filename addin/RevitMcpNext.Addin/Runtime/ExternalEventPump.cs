using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Wakes Revit for queued work (SPEC §8.2, D2 §5.4). On enqueue and on every watchdog tick (wake.watchdogMs, 200 ms)
    /// while work is pending it raises the ExternalEvent and posts WM_NULL to the main window (wake.wmNull): Revit's MFC
    /// loop starts an idle pass only after an "idle message", and WM_NULL is one, so a backgrounded or minimized Revit
    /// services the ExternalEvent without user input. No post while the main window is disabled (a modal dialog owns the
    /// loop). Three consecutive Denied/TimedOut raises flag the event for re-creation, which the Idling handler performs in
    /// API context. Settings are read on every tick (hot reload). raiseToExecMs samples feed health.pump; wakes over 1 s
    /// log a slow_wake line.
    /// </summary>
    internal sealed class ExternalEventPump : IDisposable
    {
        private const int MinPeriodMs = 50;
        private const int MaxPeriodMs = 5000;
        private const long SlowWakeMs = 1000;

        private readonly object _gate = new object();
        private readonly Func<WakeSettings> _settings;
        private readonly LatencyHistogram _raiseToExec = new LatencyHistogram();
        private readonly Dictionary<string, long> _raiseResults = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["accepted"] = 0,
            ["pending"] = 0,
            ["denied"] = 0,
            ["timedOut"] = 0
        };
        private ExternalEvent _event;
        private Timer _timer;
        private bool _armed;
        private int _period;
        private bool _disposed;
        private long _lastExecuteStartTicks;
        private int _consecutiveFailures;
        private long _wmNullPosts;
        private long _wmNullSkippedDisabled;
        private long _execViaIdling;
        private long _executeStarts;
        private long _ticks;
        private long _recreates;
        private long _raiseExceptions;

        public ExternalEventPump(Func<WakeSettings> settings)
        {
            _settings = settings ?? (() => new WakeSettings());
        }

        /// <summary>True when there is queued work, a deferred completion or a runnable job step (set by the owner).</summary>
        public Func<bool> HasWork { get; set; } = () => false;

        /// <summary>True while the UI thread executes our work (no wake-up needed).</summary>
        public Func<bool> IsExecuting { get; set; } = () => false;

        /// <summary>The Revit main window handle for WM_NULL posts (set by the owner; zero while unknown).</summary>
        public Func<IntPtr> MainWindow { get; set; } = () => IntPtr.Zero;

        /// <summary>Called on every watchdog tick while work is pending (the UI monitor samples sooner).</summary>
        public Action OnTick { get; set; }

        /// <summary>Set when Raise failed 3 times in a row; the Idling handler recreates the event.</summary>
        public bool NeedsRecreate { get; private set; }

        /// <summary>The execution path label for the next OnExecuteStart samples ("idling" while the fallback runs).</summary>
        public string CurrentVia { get; set; }

        public long WmNullPosts => Interlocked.Read(ref _wmNullPosts);
        public long ExecViaIdling => Interlocked.Read(ref _execViaIdling);
        public long Recreates => Interlocked.Read(ref _recreates);

        /// <summary>Number of Execute/Idling passes that started at least one item (used to detect fallback executions).</summary>
        public long ExecuteStarts => Interlocked.Read(ref _executeStarts);

        /// <summary>True when the owner reports work (never throws).</summary>
        public bool HasWorkNow => SafeHasWork();

        public void Attach(ExternalEvent externalEvent)
        {
            lock (_gate)
            {
                _event = externalEvent;
                _timer = _timer ?? new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>Replaces the ExternalEvent (must be created in API context, e.g. Idling).</summary>
        public void Recreate(ExternalEvent externalEvent)
        {
            ExternalEvent old;
            lock (_gate)
            {
                old = _event;
                _event = externalEvent;
                NeedsRecreate = false;
                _consecutiveFailures = 0;
            }
            Interlocked.Increment(ref _recreates);
            DiagnosticsLogger.Warn("pump", "The ExternalEvent was recreated after repeated Denied/TimedOut raises.");
            try { old?.Dispose(); } catch { }
        }

        /// <summary>Any thread: work was queued. Raises the event, posts WM_NULL and arms the watchdog.</summary>
        public void OnEnqueue()
        {
            Raise();
            PostWake();
            Arm();
        }

        /// <summary>The Idling fallback executed an item.</summary>
        public void CountIdlingExecution()
        {
            Interlocked.Increment(ref _execViaIdling);
        }

        /// <summary>UI thread: an Execute (or Idling) pass started an item; records raise-to-exec latency for the first item.</summary>
        public void OnExecuteStart(long raiseToExecMs)
        {
            Interlocked.Exchange(ref _lastExecuteStartTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _executeStarts);
            if (raiseToExecMs < 0) return;
            _raiseToExec.Add(raiseToExecMs);
            if (raiseToExecMs > SlowWakeMs) LogSlowWake(raiseToExecMs);
        }

        public void Raise()
        {
            ExternalEvent ev;
            lock (_gate) ev = _event;
            if (ev == null) return;
            try
            {
                ExternalEventRequest result = ev.Raise();
                bool flagged = false;
                lock (_gate)
                {
                    switch (result)
                    {
                        case ExternalEventRequest.Accepted: _raiseResults["accepted"]++; _consecutiveFailures = 0; break;
                        case ExternalEventRequest.Pending: _raiseResults["pending"]++; _consecutiveFailures = 0; break;
                        case ExternalEventRequest.Denied: _raiseResults["denied"]++; _consecutiveFailures++; break;
                        case ExternalEventRequest.TimedOut: _raiseResults["timedOut"]++; _consecutiveFailures++; break;
                    }
                    if (_consecutiveFailures >= 3 && !NeedsRecreate)
                    {
                        NeedsRecreate = true;
                        flagged = true;
                    }
                }
                if (flagged) DiagnosticsLogger.Warn("pump", "ExternalEvent.Raise returned " + result + " three times in a row; the event will be recreated at the next Idling.");
            }
            catch (Exception ex)
            {
                long n = Interlocked.Increment(ref _raiseExceptions);
                if (n <= 5 || n % 100 == 0) DiagnosticsLogger.Warn("pump", "ExternalEvent.Raise failed: " + ex.Message);
            }
        }

        /// <summary>Posts WM_NULL to the main window (wake.wmNull) unless it is disabled by a modal dialog.</summary>
        public void PostWake()
        {
            try
            {
                if (!SafeSettings().WmNull) return;
                IntPtr hwnd = SafeMainWindow();
                if (hwnd == IntPtr.Zero || !NativeMethods.SafeIsWindow(hwnd)) return;
                if (!NativeMethods.IsWindowEnabled(hwnd))
                {
                    Interlocked.Increment(ref _wmNullSkippedDisabled);
                    return;
                }
                if (NativeMethods.Post(hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero)) Interlocked.Increment(ref _wmNullPosts);
            }
            catch
            {
                // Advisory only.
            }
        }

        public PumpHealth GetHealth()
        {
            var health = new PumpHealth();
            _raiseToExec.Fill(health);
            lock (_gate)
            {
                foreach (KeyValuePair<string, long> pair in _raiseResults) health.RaiseResults[pair.Key] = pair.Value;
            }
            health.WmNullPosts = WmNullPosts;
            health.ExecViaIdling = ExecViaIdling;
            health.Recreates = Recreates;
            return health;
        }

        public Dictionary<string, object> GetCounters()
        {
            return new Dictionary<string, object>
            {
                ["ticks"] = Interlocked.Read(ref _ticks),
                ["executeStarts"] = ExecuteStarts,
                ["wmNullPosts"] = WmNullPosts,
                ["wmNullSkippedDisabled"] = Interlocked.Read(ref _wmNullSkippedDisabled),
                ["raiseExceptions"] = Interlocked.Read(ref _raiseExceptions),
                ["needsRecreate"] = NeedsRecreate
            };
        }

        public void Dispose()
        {
            Timer timer;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _armed = false;
                timer = _timer;
                _timer = null;
            }
            try { timer?.Dispose(); } catch { }
        }

        private void Arm()
        {
            lock (_gate)
            {
                if (_disposed || _timer == null) return;
                int period = Period();
                if (_armed && period == _period) return;
                _armed = true;
                _period = period;
                _timer.Change(period, period);
            }
        }

        private void Disarm()
        {
            lock (_gate)
            {
                if (!_armed || _timer == null) return;
                _armed = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void Tick()
        {
            try
            {
                Interlocked.Increment(ref _ticks);
                if (!SafeHasWork())
                {
                    Disarm();
                    return;
                }
                // Hot reload: a changed wake.watchdogMs re-arms the timer with the new period.
                lock (_gate)
                {
                    if (_armed && _timer != null && Period() != _period)
                    {
                        _period = Period();
                        _timer.Change(_period, _period);
                    }
                }
                try { OnTick?.Invoke(); } catch { }
                if (SafeIsExecuting()) return;
                long last = Interlocked.Read(ref _lastExecuteStartTicks);
                int watchdog = Period();
                if (last != 0 && (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalMilliseconds < watchdog) return;
                Raise();
                PostWake();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("pump", "Watchdog tick failed: " + ex.Message);
            }
        }

        private void LogSlowWake(long ms)
        {
            try
            {
                UiHealth ui = McpRuntime.UiMonitor?.GetHealth() ?? new UiHealth();
                DiagnosticsLogger.Event("slow_wake", new Dictionary<string, object>
                {
                    ["ms"] = ms,
                    ["fg"] = ui.Foreground,
                    ["minimized"] = ui.Minimized,
                    ["enabled"] = ui.MainWindowEnabled,
                    ["via"] = CurrentVia ?? "externalEvent",
                    ["wmNull"] = SafeSettings().WmNull
                });
            }
            catch
            {
                // Diagnostics only.
            }
        }

        private int Period()
        {
            return Math.Max(MinPeriodMs, Math.Min(MaxPeriodMs, SafeSettings().WatchdogMs));
        }

        private WakeSettings SafeSettings()
        {
            try { return _settings() ?? new WakeSettings(); } catch { return new WakeSettings(); }
        }

        private bool SafeHasWork()
        {
            try { return HasWork(); } catch { return false; }
        }

        private bool SafeIsExecuting()
        {
            try { return IsExecuting(); } catch { return false; }
        }

        private IntPtr SafeMainWindow()
        {
            try { return MainWindow?.Invoke() ?? IntPtr.Zero; } catch { return IntPtr.Zero; }
        }
    }
}
