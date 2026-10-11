using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Addin.Ipc;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Capture folders, retention and the result cache (SPEC §10.1): one folder per capture under
    /// &lt;home&gt;\captures\yyyy-MM-dd\HHmmss-rand6\ with image.(png|jpg) and meta.json; retention (retainHours, maxMB,
    /// maxFolders from settings.capture) runs on a background thread at the first capture after startup and then hourly;
    /// (docKey, view, generation, options) maps to a finished capture for 10 minutes.
    /// </summary>
    internal static class CaptureStore
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan MinAgeForCleanup = TimeSpan.FromMinutes(15);
        private const int MaxCacheEntries = 64;

        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, CachedCapture> Cache = new Dictionary<string, CachedCapture>(StringComparer.Ordinal);
        private static readonly object CleanupGate = new object();
        private static readonly object TimerGate = new object();
        private static Timer _cleanupTimer;
        private static readonly Random Rng = new Random();

        public static string NewFolder(McpHome home)
        {
            DateTime utc = DateTime.UtcNow;
            string stamp;
            lock (Rng) stamp = utc.ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + Rng.Next(0, 0x1000000).ToString("x6", CultureInfo.InvariantCulture);
            string folder = home.CaptureDir(utc, stamp);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>True when the path is inside &lt;home&gt;\captures (no traversal).</summary>
        public static bool IsInsideCaptures(McpHome home, string path)
        {
            try
            {
                string root = Path.GetFullPath(home.CapturesDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(path);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void WriteMeta(string folder, Dictionary<string, object> meta)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "meta.json"), JsonWireCodec.Serialize(meta));
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("capture", "Could not write meta.json: " + ex.Message);
            }
        }

        public static Dictionary<string, object> ReadMeta(string imagePath)
        {
            try
            {
                string file = Path.Combine(Path.GetDirectoryName(imagePath) ?? string.Empty, "meta.json");
                return File.Exists(file) ? JsonWireCodec.DeserializeMap(File.ReadAllText(file)) : null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------------- cache

        public static CachedCapture Lookup(string key)
        {
            lock (CacheGate)
            {
                if (!Cache.TryGetValue(key, out CachedCapture entry)) return null;
                if (entry.ExpiresUtc < DateTime.UtcNow || !File.Exists(entry.Path))
                {
                    Cache.Remove(key);
                    return null;
                }
                return entry;
            }
        }

        public static void Remember(string key, CachedCapture entry)
        {
            lock (CacheGate)
            {
                entry.ExpiresUtc = DateTime.UtcNow + CacheTtl;
                Cache[key] = entry;
                if (Cache.Count <= MaxCacheEntries) return;
                foreach (string old in Cache.OrderBy(p => p.Value.ExpiresUtc).Take(Cache.Count - MaxCacheEntries).Select(p => p.Key).ToList()) Cache.Remove(old);
            }
        }

        // ------------------------------------------------------------------------------------------------ retention

        /// <summary>Starts the retention timer once (first run now, on a pool thread; then hourly).</summary>
        public static void EnsureRetention(McpHome home)
        {
            if (home == null) return;
            lock (TimerGate)
            {
                if (_cleanupTimer != null) return;
                _cleanupTimer = new Timer(_ => Cleanup(home), null, TimeSpan.FromSeconds(5), CleanupInterval);
            }
        }

        private static void Cleanup(McpHome home)
        {
            if (!Monitor.TryEnter(CleanupGate)) return;
            try
            {
                CaptureSettings settings = McpRuntime.CurrentSettings?.Capture ?? new CaptureSettings();
                string root = home.CapturesDir;
                if (!Directory.Exists(root)) return;
                var folders = new List<KeyValuePair<DirectoryInfo, long>>();
                foreach (DirectoryInfo day in new DirectoryInfo(root).GetDirectories())
                {
                    foreach (DirectoryInfo folder in day.GetDirectories())
                    {
                        long bytes = 0;
                        try { bytes = folder.GetFiles().Sum(f => f.Length); } catch { bytes = 0; }
                        folders.Add(new KeyValuePair<DirectoryInfo, long>(folder, bytes));
                    }
                }
                DateTime now = DateTime.UtcNow;
                DateTime cutoff = now.AddHours(-Math.Max(1, settings.RetainHours));
                long maxBytes = (long)Math.Max(16, settings.MaxMB) * 1024 * 1024;
                int maxFolders = Math.Max(10, settings.MaxFolders);
                List<KeyValuePair<DirectoryInfo, long>> ordered = folders.OrderBy(f => f.Key.CreationTimeUtc).ToList();
                long total = ordered.Sum(f => f.Value);
                int count = ordered.Count, deleted = 0;
                foreach (KeyValuePair<DirectoryInfo, long> folder in ordered)
                {
                    DateTime created = folder.Key.CreationTimeUtc;
                    if (now - created < MinAgeForCleanup) break;
                    bool expired = created < cutoff;
                    if (!expired && count <= maxFolders && total <= maxBytes) break;
                    try
                    {
                        folder.Key.Delete(true);
                        count--;
                        total -= folder.Value;
                        deleted++;
                    }
                    catch
                    {
                        // In use or locked; try again next hour.
                    }
                }
                foreach (DirectoryInfo day in new DirectoryInfo(root).GetDirectories())
                {
                    try
                    {
                        if (!day.EnumerateFileSystemInfos().Any() && now - day.CreationTimeUtc > MinAgeForCleanup) day.Delete(false);
                    }
                    catch
                    {
                        // Ignore.
                    }
                }
                if (deleted > 0) DiagnosticsLogger.Info("capture", "Capture retention removed " + deleted.ToString(CultureInfo.InvariantCulture) + " folder(s).");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("capture", "Capture retention failed: " + ex.Message);
            }
            finally
            {
                Monitor.Exit(CleanupGate);
            }
        }
    }

    internal sealed class CachedCapture
    {
        public string Path { get; set; }
        public string Mime { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }
        public Dictionary<string, object> Meta { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public string Summary { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
}
