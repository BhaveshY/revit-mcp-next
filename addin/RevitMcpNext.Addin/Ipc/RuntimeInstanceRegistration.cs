using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin.Ipc
{
    internal sealed class RuntimeInstanceRegistration : IDisposable
    {
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
        private readonly object _gate = new object();
        private readonly string _instanceId;
        private readonly string _pipeName;
        private readonly string _revitVersion;
        private readonly string _revitBuild;
        private readonly string _addinVersion;
        private readonly int _processId;
        private readonly DateTimeOffset _startedAtUtc;
        private readonly string _registrationPath;
        private Timer _heartbeat;
        private bool _disposed;

        private RuntimeInstanceRegistration(
            string instanceId,
            string pipeName,
            string revitVersion,
            string revitBuild,
            string addinVersion)
        {
            _instanceId = instanceId;
            _pipeName = pipeName;
            _revitVersion = revitVersion ?? string.Empty;
            _revitBuild = revitBuild ?? string.Empty;
            _addinVersion = addinVersion ?? string.Empty;
            Process process = Process.GetCurrentProcess();
            _processId = process.Id;
            try
            {
                _startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            }
            catch
            {
                _startedAtUtc = DateTimeOffset.UtcNow;
            }

            string registryRoot = ResolveRegistryRoot();
            Directory.CreateDirectory(registryRoot);
            _registrationPath = Path.Combine(registryRoot, _instanceId + ".json");
        }

        public static RuntimeInstanceRegistration Start(
            string instanceId,
            string pipeName,
            string revitVersion,
            string revitBuild,
            string addinVersion)
        {
            var registration = new RuntimeInstanceRegistration(instanceId, pipeName, revitVersion, revitBuild, addinVersion);
            registration.WriteSnapshot();
            registration._heartbeat = new Timer(_ => registration.TryHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);
            return registration;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _heartbeat?.Dispose();
                _heartbeat = null;
                try
                {
                    if (File.Exists(_registrationPath)) File.Delete(_registrationPath);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("Failed to remove Revit MCP runtime instance registration.", ex);
                }
            }
        }

        private void TryHeartbeat()
        {
            try
            {
                WriteSnapshot();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("Failed to refresh Revit MCP runtime instance registration.", ex);
            }
        }

        private void WriteSnapshot()
        {
            lock (_gate)
            {
                if (_disposed) return;

                var metadata = new Dictionary<string, object>
                {
                    ["schemaVersion"] = 1,
                    ["instanceId"] = _instanceId,
                    ["pipeName"] = _pipeName,
                    ["controlPipeName"] = _pipeName + "-control",
                    ["processId"] = _processId,
                    ["revitVersion"] = _revitVersion,
                    ["revitBuild"] = _revitBuild,
                    ["addinVersion"] = _addinVersion,
                    ["startedAtUtc"] = _startedAtUtc.ToUniversalTime().ToString("o"),
                    ["lastSeenAtUtc"] = DateTimeOffset.UtcNow.ToUniversalTime().ToString("o")
                };

                string temporaryPath = _registrationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, JsonWireCodec.Serialize(metadata), new UTF8Encoding(false));
                    if (File.Exists(_registrationPath))
                    {
                        try
                        {
                            File.Replace(temporaryPath, _registrationPath, null);
                        }
                        catch (IOException)
                        {
                            // A broker may briefly hold the destination while reading it on
                            // Windows. Preserve the last valid snapshot until an overwrite can
                            // complete; the broker treats a transient partial read as retryable.
                            File.Copy(temporaryPath, _registrationPath, true);
                        }
                    }
                    else
                    {
                        File.Move(temporaryPath, _registrationPath);
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // A stale .tmp file is ignored by discovery and can be cleaned later.
                    }
                }
            }
        }

        private static string ResolveRegistryRoot()
        {
            string configured = Environment.GetEnvironmentVariable("REVIT_MCP_NEXT_INSTANCE_REGISTRY");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMcpNext",
                "instances");
        }
    }
}
