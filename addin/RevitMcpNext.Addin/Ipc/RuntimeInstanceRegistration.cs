using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    /// <summary>
    /// Instance registration (D2 §2.4): &lt;home&gt;\instances\&lt;instanceId&gt;.json (the DocSnapshot plus writtenAtUtc, at
    /// most 64 KB) and &lt;instanceId&gt;.lock held with FileShare.None for the process lifetime (the liveness signal; the OS
    /// releases it on a crash). Atomic writes only (tmp + File.Replace with retries, never File.Copy); written at start,
    /// 250 ms after snapshot changes (debounced) and every 30 s.
    /// </summary>
    internal sealed class RuntimeInstanceRegistration : IDisposable
    {
        private const int DebounceMs = 250;
        private const int HeartbeatMs = 30000;
        private const int MaxBytes = 64 * 1024;
        private readonly object _gate = new object();
        private readonly McpHome _home;
        private readonly InstanceIdentity _identity;
        private readonly DocumentRegistry _registry;
        private readonly string _jsonPath;
        private readonly string _lockPath;
        private FileStream _lock;
        private Timer _debounce;
        private Timer _heartbeat;
        private bool _disposed;
        private long _writes;
        private long _failures;

        private RuntimeInstanceRegistration(McpHome home, InstanceIdentity identity, DocumentRegistry registry)
        {
            _home = home;
            _identity = identity;
            _registry = registry;
            _jsonPath = home.InstanceFile(identity.InstanceId);
            _lockPath = home.LockFile(identity.InstanceId);
        }

        public string FilePath => _jsonPath;
        public string LockPath => _lockPath;
        public bool LockHeld => _lock != null;

        /// <summary>Takes the lock, writes the first registration (state from the registry) and starts the timers.</summary>
        public static RuntimeInstanceRegistration Start(McpHome home, InstanceIdentity identity, DocumentRegistry registry)
        {
            var registration = new RuntimeInstanceRegistration(home, identity, registry);
            Directory.CreateDirectory(home.InstancesDir);
            registration.AcquireLock();
            registration.WriteNow();
            registration._debounce = new Timer(_ => registration.SafeWrite(), null, Timeout.Infinite, Timeout.Infinite);
            registration._heartbeat = new Timer(_ => registration.SafeWrite(), null, HeartbeatMs, HeartbeatMs);
            registry.SnapshotChanged += registration.OnSnapshotChanged;
            return registration;
        }

        public void WriteNow()
        {
            DocSnapshot snapshot = _registry.Current.Clone();
            snapshot.WrittenAtUtc = DocumentRegistry.Utc(DateTime.UtcNow);
            string json = SerializeCapped(snapshot);
            lock (_gate)
            {
                if (_disposed && snapshot.State != "stopping") return;
                if (AtomicFile.WriteAllText(_jsonPath, json, out string error))
                {
                    _writes++;
                }
                else
                {
                    _failures++;
                    DiagnosticsLogger.Warn("registration", "Could not write " + _jsonPath + " (kept the previous file): " + error);
                }
            }
        }

        public Dictionary<string, object> GetCounters()
        {
            lock (_gate)
            {
                return new Dictionary<string, object> { ["writes"] = _writes, ["failures"] = _failures, ["lockHeld"] = _lock != null };
            }
        }

        /// <summary>Writes the final state (the caller sets "stopping" first), deletes the JSON and releases the lock.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
            }
            _registry.SnapshotChanged -= OnSnapshotChanged;
            SafeWrite();
            lock (_gate) _disposed = true;
            try { _debounce?.Dispose(); } catch { }
            try { _heartbeat?.Dispose(); } catch { }
            try
            {
                if (File.Exists(_jsonPath)) File.Delete(_jsonPath);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("registration", "Could not delete " + _jsonPath + ": " + ex.Message);
            }
            try
            {
                _lock?.Dispose();
            }
            catch
            {
                // The lock is DeleteOnClose; the OS cleans up.
            }
            _lock = null;
        }

        private void AcquireLock()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                    byte[] pid = Encoding.ASCII.GetBytes(_identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _lock.Write(pid, 0, pid.Length);
                    _lock.Flush(true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("registration", "Could not take the instance lock " + _lockPath + "; brokers may treat this instance as dead.", ex);
                    return;
                }
            }
        }

        private void OnSnapshotChanged(DocSnapshot snapshot)
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _debounce?.Change(DebounceMs, Timeout.Infinite);
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }

        private void SafeWrite()
        {
            try
            {
                WriteNow();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("registration", "Registration write failed.", ex);
            }
        }

        /// <summary>Serializes the snapshot within 64 KB: trims levels first, then the docs list.</summary>
        private static string SerializeCapped(DocSnapshot snapshot)
        {
            string json = JsonWireCodec.Serialize(snapshot.ToWire());
            if (Encoding.UTF8.GetByteCount(json) <= MaxBytes) return json;

            DocSnapshot trimmed = snapshot.Clone();
            trimmed.Docs = trimmed.Docs.Select(doc =>
            {
                SnapshotDoc copy = doc.Clone();
                copy.LevelsMore += copy.Levels.Count;
                copy.Levels = new List<LevelRow>();
                return copy;
            }).ToList();
            json = JsonWireCodec.Serialize(trimmed.ToWire());
            while (Encoding.UTF8.GetByteCount(json) > MaxBytes && trimmed.Docs.Count > 1)
            {
                trimmed.Docs.RemoveAt(trimmed.Docs.Count - 1);
                json = JsonWireCodec.Serialize(trimmed.ToWire());
            }
            return json;
        }
    }
}
