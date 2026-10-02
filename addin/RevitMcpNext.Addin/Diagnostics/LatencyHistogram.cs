using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpNext.Addin.Diagnostics
{
    /// <summary>Ring of the last 512 latency samples (10-minute window) with percentile summaries (D2 §5.5).</summary>
    internal sealed class LatencyHistogram
    {
        private const int Capacity = 512;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        private readonly object _gate = new object();
        private readonly long[] _values = new long[Capacity];
        private readonly DateTime[] _times = new DateTime[Capacity];
        private int _next;
        private int _count;
        private long _total;
        private long _over1s;

        public void Add(long milliseconds)
        {
            if (milliseconds < 0) milliseconds = 0;
            lock (_gate)
            {
                _values[_next] = milliseconds;
                _times[_next] = DateTime.UtcNow;
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
                _total++;
                if (milliseconds > 1000) _over1s++;
            }
        }

        /// <summary>Fills p50/p90/p99/max over the window and the cumulative n/over1s.</summary>
        public void Fill(RevitMcpNext.Contracts.PumpHealth health)
        {
            if (health == null) return;
            List<long> samples;
            lock (_gate)
            {
                DateTime cutoff = DateTime.UtcNow - Window;
                samples = new List<long>(_count);
                for (int i = 0; i < _count; i++)
                {
                    if (_times[i] >= cutoff) samples.Add(_values[i]);
                }
                health.N = _total;
                health.Over1s = _over1s;
            }
            if (samples.Count == 0) return;
            samples.Sort();
            health.P50 = Percentile(samples, 0.50);
            health.P90 = Percentile(samples, 0.90);
            health.P99 = Percentile(samples, 0.99);
            health.Max = samples[samples.Count - 1];
        }

        private static long Percentile(List<long> sorted, double p)
        {
            int index = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }
    }
}
