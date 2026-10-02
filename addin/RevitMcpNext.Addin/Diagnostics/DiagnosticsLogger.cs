using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using RevitMcpNext.Addin.Ipc;

namespace RevitMcpNext.Addin.Diagnostics
{
    /// <summary>
    /// Background JSONL logger (D2 §14.3, D3 §4 #17): &lt;home&gt;\logs\addin-&lt;year&gt;-YYYYMMDD.&lt;n&gt;.jsonl, rotated by size
    /// (log.maxFileMb) and day, retained log.retainDays. Callers never do file I/O: lines go to a bounded queue drained
    /// by one background thread. Lines logged before <see cref="Initialize"/> are buffered and written afterwards.
    /// Never throws.
    /// </summary>
    internal static class DiagnosticsLogger
    {
        private const int QueueCapacity = 20000;
        private static readonly BlockingCollection<string> Queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), QueueCapacity);
        private static readonly object InitGate = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static Thread _writer;
        private static volatile bool _initialized;
        private static McpHome _home;
        private static int _year;
        private static Func<LogSettings> _settings = () => new LogSettings();
        private static long _dropped;
        private static DateTime _lastRetentionUtc = DateTime.MinValue;

        /// <summary>Starts the writer thread. Safe to call once; later calls only update the settings accessor.</summary>
        public static void Initialize(McpHome home, int year, Func<LogSettings> settings)
        {
            lock (InitGate)
            {
                if (settings != null) _settings = settings;
                if (_initialized) return;
                _home = home;
                _year = year;
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "RevitMcpNext log writer", Priority = ThreadPriority.BelowNormal };
                _initialized = true;
                _writer.Start();
            }
        }

        /// <summary>The current log file pattern, for error details (logPath).</summary>
        public static string LogDirectory => _home?.LogsDir ?? string.Empty;

        public static string CurrentLogFile { get; private set; } = string.Empty;

        public static void Debug(string category, string message)
        {
            if (!LevelEnabled("debug")) return;
            Enqueue("debug", category, message, null, null);
        }

        public static void Info(string message)
        {
            Enqueue("info", "addin", message, null, null);
        }

        public static void Info(string category, string message, Dictionary<string, object> fields = null)
        {
            if (!LevelEnabled("info")) return;
            Enqueue("info", category, message, fields, null);
        }

        public static void Warn(string category, string message, Dictionary<string, object> fields = null)
        {
            if (!LevelEnabled("warn")) return;
            Enqueue("warn", category, message, fields, null);
        }

        public static void Error(string message, Exception exception = null)
        {
            Enqueue("error", "addin", message, null, exception);
        }

        public static void Error(string category, string message, Exception exception, Dictionary<string, object> fields = null)
        {
            Enqueue("error", category, message, fields, exception);
        }

        /// <summary>Structured event line, e.g. evt "request" with requestId/op/queueWaitMs/execMs/via/code.</summary>
        public static void Event(string evt, Dictionary<string, object> fields)
        {
            if (!LevelEnabled("info")) return;
            Enqueue("info", evt, null, fields, null);
        }

        /// <summary>Waits up to <paramref name="timeoutMs"/> for queued lines to be written (shutdown).</summary>
        public static void Flush(int timeoutMs = 2000)
        {
            if (!_initialized) return;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Queue.Count > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }

        private static bool LevelEnabled(string level)
        {
            string configured;
            try
            {
                configured = _settings()?.Level ?? "info";
            }
            catch
            {
                configured = "info";
            }
            return Rank(level) >= Rank(configured);
        }

        private static int Rank(string level)
        {
            switch (level)
            {
                case "debug": return 0;
                case "info": return 1;
                case "warn": return 2;
                case "error": return 3;
                default: return 1;
            }
        }

        private static void Enqueue(string level, string category, string message, Dictionary<string, object> fields, Exception exception)
        {
            try
            {
                var line = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ts"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["lvl"] = level,
                    ["evt"] = category ?? "addin",
                    ["pid"] = CurrentPid
                };
                if (!string.IsNullOrEmpty(message)) line["msg"] = message;
                if (fields != null)
                {
                    foreach (KeyValuePair<string, object> pair in fields)
                    {
                        if (!line.ContainsKey(pair.Key)) line[pair.Key] = pair.Value;
                    }
                }
                if (exception != null)
                {
                    line["ex"] = exception.GetType().FullName + ": " + exception.Message;
                    line["stack"] = Truncate(exception.ToString(), 8000);
                }

                string json;
                try
                {
                    json = JsonWireCodec.Serialize(line);
                }
                catch
                {
                    json = "{\"ts\":\"" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\",\"lvl\":\"" + level +
                           "\",\"msg\":\"(log line could not be serialized)\"}";
                }

                if (!Queue.TryAdd(json)) Interlocked.Increment(ref _dropped);
            }
            catch
            {
                // Diagnostics must never affect Revit automation.
            }
        }

        private static readonly int CurrentPid = GetPid();

        private static int GetPid()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }

        private static void WriterLoop()
        {
            StreamWriter writer = null;
            string currentPath = null;
            DateTime currentDay = DateTime.MinValue;
            int index = 0;
            long written = 0;
            try
            {
                foreach (string line in Queue.GetConsumingEnumerable())
                {
                    try
                    {
                        DateTime now = DateTime.UtcNow;
                        long maxBytes = Math.Max(1, SafeSettings().MaxFileMb) * 1024L * 1024L;
                        if (writer == null || now.Date != currentDay || written >= maxBytes)
                        {
                            writer?.Dispose();
                            writer = null;
                            if (now.Date != currentDay)
                            {
                                currentDay = now.Date;
                                index = 0;
                            }
                            Directory.CreateDirectory(_home.LogsDir);
                            // Find the next file with room (another Revit process of the same year may share the day).
                            while (true)
                            {
                                currentPath = _home.AddinLogFile(_year, currentDay, index);
                                long existing = File.Exists(currentPath) ? new FileInfo(currentPath).Length : 0;
                                if (existing < maxBytes) { written = existing; break; }
                                index++;
                            }
                            var stream = new FileStream(currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                            writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
                            CurrentLogFile = currentPath;
                            ApplyRetention();
                        }

                        writer.WriteLine(line);
                        written += Utf8NoBom.GetByteCount(line) + 2;
                        if (Queue.Count == 0) writer.Flush();

                        long dropped = Interlocked.Exchange(ref _dropped, 0);
                        if (dropped > 0)
                        {
                            writer.WriteLine("{\"ts\":\"" + now.ToString("O", CultureInfo.InvariantCulture) +
                                             "\",\"lvl\":\"warn\",\"evt\":\"log\",\"msg\":\"" + dropped.ToString(CultureInfo.InvariantCulture) +
                                             " log lines were dropped (queue full).\"}");
                        }
                    }
                    catch
                    {
                        try { writer?.Dispose(); } catch { }
                        writer = null;
                        Thread.Sleep(250);
                    }
                }
            }
            catch
            {
                // The writer thread must never take Revit down.
            }
            finally
            {
                try { writer?.Dispose(); } catch { }
            }
        }

        private static LogSettings SafeSettings()
        {
            try
            {
                return _settings() ?? new LogSettings();
            }
            catch
            {
                return new LogSettings();
            }
        }

        private static void ApplyRetention()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastRetentionUtc).TotalHours < 6) return;
            _lastRetentionUtc = now;
            try
            {
                int days = Math.Max(1, SafeSettings().RetainDays);
                foreach (string file in Directory.GetFiles(_home.LogsDir, "addin-*.jsonl"))
                {
                    try
                    {
                        if ((now - File.GetLastWriteTimeUtc(file)).TotalDays > days) File.Delete(file);
                    }
                    catch
                    {
                        // Locked by another process; retry at the next rotation.
                    }
                }
            }
            catch
            {
                // Best effort.
            }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "...(+" + (text.Length - max).ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
