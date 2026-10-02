using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Wakes Revit for queued work (SPEC §8.2, D2 §5.4). Minimal wave-1 version: Raise on enqueue plus a re-raise
    /// watchdog (wake.watchdogMs, default 200 ms) while work is pending. P-REL-ADDIN adds WM_NULL posts, the Idling
    /// fallback and ExternalEvent re-creation; the public members below are the seam it builds on.
    /// </summary>
    internal sealed class ExternalEventPump : IDisposable
    {
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
        private bool _disposed;
        private long _lastExecuteStartTicks;
        private int _consecutiveFailures;

        public ExternalEventPump(Func<WakeSettings> settings)
        {
            _settings = settings ?? (() => new WakeSettings());
        }

        /// <summary>True when there is queued work, a deferred completion or a runnable job step (set by the owner).</summary>
        public Func<bool> HasWork { get; set; } = () => false;

        /// <summary>True while the UI thread executes our work (no wake-up needed).</summary>
        public Func<bool> IsExecuting { get; set; } = () => false;

        /// <summary>Set when Raise failed 3 times in a row; the Idling handler may recreate the event.</summary>
        public bool NeedsRecreate { get; private set; }

        public long WmNullPosts { get; set; }
        public long ExecViaIdling { get; set; }
        public long Recreates { get; private set; }

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
                Recreates++;
            }
            try { old?.Dispose(); } catch { }
        }

        /// <summary>Any thread: work was queued. Raises the event and arms the watchdog.</summary>
        public void OnEnqueue()
        {
            Raise();
            Arm();
        }

        /// <summary>UI thread: an Execute (or Idling) pass started; records raise-to-exec latency for the first item.</summary>
        public void OnExecuteStart(long raiseToExecMs)
        {
            Interlocked.Exchange(ref _lastExecuteStartTicks, DateTime.UtcNow.Ticks);
            if (raiseToExecMs >= 0) _raiseToExec.Add(raiseToExecMs);
        }

        public void Raise()
        {
            ExternalEvent ev;
            lock (_gate) ev = _event;
            if (ev == null) return;
            try
            {
                ExternalEventRequest result = ev.Raise();
                lock (_gate)
                {
                    switch (result)
                    {
                        case ExternalEventRequest.Accepted: _raiseResults["accepted"]++; _consecutiveFailures = 0; break;
                        case ExternalEventRequest.Pending: _raiseResults["pending"]++; _consecutiveFailures = 0; break;
                        case ExternalEventRequest.Denied: _raiseResults["denied"]++; _consecutiveFailures++; break;
                        case ExternalEventRequest.TimedOut: _raiseResults["timedOut"]++; _consecutiveFailures++; break;
                    }
                    if (_consecutiveFailures >= 3) NeedsRecreate = true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("pump", "ExternalEvent.Raise failed: " + ex.Message);
            }
        }

        public PumpHealth GetHealth()
        {
            var health = new PumpHealth();
            _raiseToExec.Fill(health);
            lock (_gate)
            {
                foreach (KeyValuePair<string, long> pair in _raiseResults) health.RaiseResults[pair.Key] = pair.Value;
                health.WmNullPosts = WmNullPosts;
                health.ExecViaIdling = ExecViaIdling;
                health.Recreates = Recreates;
            }
            return health;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _armed = false;
            }
            try { _timer?.Dispose(); } catch { }
        }

        private void Arm()
        {
            lock (_gate)
            {
                if (_disposed || _armed || _timer == null) return;
                _armed = true;
                int period = Math.Max(50, _settings().WatchdogMs);
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
                if (!SafeHasWork())
                {
                    Disarm();
                    return;
                }
                if (SafeIsExecuting()) return;
                long last = Interlocked.Read(ref _lastExecuteStartTicks);
                int watchdog = Math.Max(50, _settings().WatchdogMs);
                if (last != 0 && (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalMilliseconds < watchdog) return;
                Raise();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("pump", "Watchdog tick failed: " + ex.Message);
            }
        }

        private bool SafeHasWork()
        {
            try { return HasWork(); } catch { return false; }
        }

        private bool SafeIsExecuting()
        {
            try { return IsExecuting(); } catch { return false; }
        }
    }
}
