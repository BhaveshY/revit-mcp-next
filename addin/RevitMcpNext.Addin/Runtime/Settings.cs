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
    /// config\settings.json (SPEC §3.1, plus codeExecution.consentPrompt from the lead overrides). Immutable snapshot:
    /// a reload builds a new instance. Missing keys take the defaults below; out-of-range numbers are clamped.
    /// Mirrors contracts/src/home.ts (settings defaults and validator).
    /// </summary>
    internal sealed class McpSettings
    {
        public int SchemaVersion { get; private set; } = 1;
        /// <summary>Changed only by the installer or the user editing the file; never by a tool argument.</summary>
        public bool EnableCodeExecution { get; private set; }
        public CodeExecutionSettings CodeExecution { get; private set; } = new CodeExecutionSettings();
        /// <summary>Per-call budget, clamped to 10000-55000 ms.</summary>
        public int CallBudgetMs { get; private set; } = 50000;
        public int PerInstancePrimarySlots { get; private set; } = 4;
        public WakeSettings Wake { get; private set; } = new WakeSettings();
        public StallSettings Stall { get; private set; } = new StallSettings();
        public DialogPolicySettings DialogPolicy { get; private set; } = new DialogPolicySettings();
        public ConfirmSettings Confirm { get; private set; } = new ConfirmSettings();
        public CaptureSettings Capture { get; private set; } = new CaptureSettings();
        public LogSettings Log { get; private set; } = new LogSettings();
        public bool UseElicitation { get; private set; }
        public bool ExperimentalUndoStack { get; private set; }
        /// <summary>Default project template per Revit year ("2024", "2027"); empty means the English metric template.</summary>
        public IReadOnlyDictionary<string, string> DefaultTemplate { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal) { ["2024"] = string.Empty, ["2027"] = string.Empty };

        public static McpSettings Defaults { get; } = new McpSettings();

        /// <summary>The configured default template for a year, or null when not set.</summary>
        public string DefaultTemplateFor(int year)
        {
            return DefaultTemplate.TryGetValue(year.ToString(CultureInfo.InvariantCulture), out string path) &&
                   !string.IsNullOrWhiteSpace(path)
                ? path.Trim()
                : null;
        }

        /// <summary>Parses a settings object; problems (unknown types, clamped values) are appended to warnings.</summary>
        public static McpSettings FromJson(IDictionary<string, object> root, List<string> warnings)
        {
            var reader = new SettingsReader(root ?? new Dictionary<string, object>(), warnings ?? new List<string>());
            var settings = new McpSettings
            {
                SchemaVersion = reader.Int("schemaVersion", 1, 1, 1000),
                EnableCodeExecution = reader.Bool("enableCodeExecution", false),
                CallBudgetMs = reader.Int("callBudgetMs", 50000, 10000, 55000),
                PerInstancePrimarySlots = reader.Int("perInstancePrimarySlots", 4, 1, 16),
                UseElicitation = reader.Bool("useElicitation", false),
                ExperimentalUndoStack = reader.Bool("experimentalUndoStack", false)
            };

            SettingsReader code = reader.Section("codeExecution");
            settings.CodeExecution = new CodeExecutionSettings
            {
                TimeoutSec = code.Int("timeoutSec", 30, 1, 600),
                MaxOutputKB = code.Int("maxOutputKB", 64, 1, 4096),
                AllowUnsafeApis = code.Bool("allowUnsafeApis", false),
                ExperimentalOutOfProcess = code.Bool("experimentalOutOfProcess", false),
                ConsentPrompt = code.Bool("consentPrompt", false),
                E2ePreapproved = code.Bool("e2ePreapproved", false)
            };

            SettingsReader wake = reader.Section("wake");
            settings.Wake = new WakeSettings
            {
                WatchdogMs = wake.Int("watchdogMs", 200, 50, 5000),
                WmNull = wake.Bool("wmNull", true),
                IdlingFallback = wake.Bool("idlingFallback", true),
                IdlingFallbackAfterMs = wake.Int("idlingFallbackAfterMs", 1000, 100, 60000)
            };

            SettingsReader stall = reader.Section("stall");
            settings.Stall = new StallSettings
            {
                DialogMs = stall.Int("dialogMs", 3000, 500, 60000),
                HungMs = stall.Int("hungMs", 5000, 1000, 60000),
                EditModeMs = stall.Int("editModeMs", 6000, 1000, 60000),
                BusyFailFastMs = stall.Int("busyFailFastMs", 8000, 1000, 60000)
            };

            SettingsReader dialogs = reader.Section("dialogPolicy");
            List<string> deny = dialogs.Strings("deny");
            var extra = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in dialogs.Map("extra"))
            {
                string id = (pair.Key ?? string.Empty).Trim();
                if (id.Length == 0) continue;
                if (id.StartsWith("RevitMcpNext_", StringComparison.OrdinalIgnoreCase))
                {
                    warnings?.Add("dialogPolicy.extra cannot answer our own dialog " + id + "; ignored.");
                    continue;
                }
                if (deny.Any(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings?.Add("dialogPolicy.extra id " + id + " is on dialogPolicy.deny; ignored.");
                    continue;
                }
                if (!TryInt(pair.Value, out int button))
                {
                    warnings?.Add("dialogPolicy.extra." + id + " must be a button id number; ignored.");
                    continue;
                }
                extra[id] = button;
            }
            settings.DialogPolicy = new DialogPolicySettings
            {
                AutoRespond = dialogs.Bool("autoRespond", true),
                Extra = extra,
                Deny = deny
            };

            SettingsReader confirm = reader.Section("confirm");
            settings.Confirm = new ConfirmSettings
            {
                DeleteOver = confirm.Int("deleteOver", 20, 0, 1000000),
                BulkOver = confirm.Int("bulkOver", 200, 0, 1000000),
                CreateOver = confirm.Int("createOver", 500, 0, 1000000)
            };

            SettingsReader capture = reader.Section("capture");
            settings.Capture = new CaptureSettings
            {
                Size = capture.Enum("size", "medium", "small", "medium", "large"),
                Format = capture.Enum("format", "auto", "auto", "png", "jpg"),
                RetainHours = capture.Int("retainHours", 72, 1, 24 * 365),
                MaxMB = capture.Int("maxMB", 1024, 16, 1024 * 64),
                MaxFolders = capture.Int("maxFolders", 500, 10, 100000)
            };

            SettingsReader log = reader.Section("log");
            settings.Log = new LogSettings
            {
                Level = log.Enum("level", "info", "debug", "info", "warn", "error"),
                MaxFileMb = log.Int("maxFileMb", 10, 1, 1024),
                RetainDays = log.Int("retainDays", 14, 1, 3650)
            };

            var templates = new Dictionary<string, string>(StringComparer.Ordinal) { ["2024"] = string.Empty, ["2027"] = string.Empty };
            foreach (KeyValuePair<string, object> pair in reader.Map("defaultTemplate"))
            {
                templates[pair.Key] = pair.Value as string ?? string.Empty;
            }
            settings.DefaultTemplate = templates;
            return settings;
        }

        internal static bool TryInt(object value, out int result)
        {
            result = 0;
            if (value == null || value is bool) return false;
            try
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) return false;
                result = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(number)));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class SettingsReader
        {
            private readonly IDictionary<string, object> _map;
            private readonly List<string> _warnings;
            private readonly string _prefix;

            public SettingsReader(IDictionary<string, object> map, List<string> warnings, string prefix = "")
            {
                _map = map ?? new Dictionary<string, object>();
                _warnings = warnings;
                _prefix = prefix;
            }

            public SettingsReader Section(string key)
            {
                object value = Get(key);
                if (value != null && !(value is IDictionary<string, object>))
                {
                    _warnings.Add(_prefix + key + " must be an object; defaults used.");
                }
                return new SettingsReader(value as IDictionary<string, object>, _warnings, _prefix + key + ".");
            }

            public IDictionary<string, object> Map(string key)
            {
                object value = Get(key);
                if (value is IDictionary<string, object> map) return map;
                if (value != null) _warnings.Add(_prefix + key + " must be an object; ignored.");
                return new Dictionary<string, object>();
            }

            public int Int(string key, int defaultValue, int min, int max)
            {
                object value = Get(key);
                if (value == null) return defaultValue;
                if (!TryInt(value, out int number))
                {
                    _warnings.Add(_prefix + key + " must be a number; default " + defaultValue.ToString(CultureInfo.InvariantCulture) + " used.");
                    return defaultValue;
                }
                if (number < min || number > max)
                {
                    int clamped = Math.Max(min, Math.Min(max, number));
                    _warnings.Add(_prefix + key + " clamped to " + clamped.ToString(CultureInfo.InvariantCulture) + ".");
                    return clamped;
                }
                return number;
            }

            public bool Bool(string key, bool defaultValue)
            {
                object value = Get(key);
                if (value == null) return defaultValue;
                if (value is bool flag) return flag;
                string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant();
                if (text == "true" || text == "1" || text == "yes") return true;
                if (text == "false" || text == "0" || text == "no") return false;
                _warnings.Add(_prefix + key + " must be true or false; default " + (defaultValue ? "true" : "false") + " used.");
                return defaultValue;
            }

            public string Enum(string key, string defaultValue, params string[] allowed)
            {
                object value = Get(key);
                if (value == null) return defaultValue;
                string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant();
                if (allowed.Contains(text)) return text;
                _warnings.Add(_prefix + key + " must be one of " + string.Join("|", allowed) + "; default " + defaultValue + " used.");
                return defaultValue;
            }

            public List<string> Strings(string key)
            {
                object value = Get(key);
                var result = new List<string>();
                if (value == null) return result;
                if (value is string single)
                {
                    if (!string.IsNullOrWhiteSpace(single)) result.Add(single.Trim());
                    return result;
                }
                if (value is System.Collections.IEnumerable list)
                {
                    foreach (object item in list)
                    {
                        string text = Convert.ToString(item, CultureInfo.InvariantCulture)?.Trim();
                        if (!string.IsNullOrEmpty(text)) result.Add(text);
                    }
                    return result;
                }
                _warnings.Add(_prefix + key + " must be a list of strings; ignored.");
                return result;
            }

            private object Get(string key)
            {
                return _map.TryGetValue(key, out object value) ? value : null;
            }
        }
    }

    internal sealed class CodeExecutionSettings
    {
        public int TimeoutSec { get; set; } = 30;
        public int MaxOutputKB { get; set; } = 64;
        public bool AllowUnsafeApis { get; set; }
        public bool ExperimentalOutOfProcess { get; set; }
        /// <summary>When false (default) the per-Revit-session consent TaskDialog is skipped (lead override 5).</summary>
        public bool ConsentPrompt { get; set; }
        /// <summary>e2e only: honoured only in isolated homes with config\e2e-test-ops.enable (SPEC §13.4).</summary>
        public bool E2ePreapproved { get; set; }
    }

    internal sealed class WakeSettings
    {
        public int WatchdogMs { get; set; } = 200;
        public bool WmNull { get; set; } = true;
        public bool IdlingFallback { get; set; } = true;
        public int IdlingFallbackAfterMs { get; set; } = 1000;
    }

    internal sealed class StallSettings
    {
        public int DialogMs { get; set; } = 3000;
        public int HungMs { get; set; } = 5000;
        public int EditModeMs { get; set; } = 6000;
        public int BusyFailFastMs { get; set; } = 8000;
    }

    internal sealed class DialogPolicySettings
    {
        public bool AutoRespond { get; set; } = true;
        /// <summary>Extra DialogId -> button id answers; RevitMcpNext_ ids and denylisted ids are already removed.</summary>
        public IReadOnlyDictionary<string, int> Extra { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public IReadOnlyList<string> Deny { get; set; } = new List<string>();
    }

    internal sealed class ConfirmSettings
    {
        public int DeleteOver { get; set; } = 20;
        public int BulkOver { get; set; } = 200;
        public int CreateOver { get; set; } = 500;
    }

    internal sealed class CaptureSettings
    {
        public string Size { get; set; } = "medium";
        public string Format { get; set; } = "auto";
        public int RetainHours { get; set; } = 72;
        public int MaxMB { get; set; } = 1024;
        public int MaxFolders { get; set; } = 500;
    }

    internal sealed class LogSettings
    {
        public string Level { get; set; } = "info";
        public int MaxFileMb { get; set; } = 10;
        public int RetainDays { get; set; } = 14;
    }

    /// <summary>
    /// Loads settings.json and hot-reloads it (FileSystemWatcher, 500 ms debounce). Invalid JSON keeps the last good
    /// values and sets <see cref="Problem"/> (reported as SETTINGS_INVALID). Thread-safe: <see cref="Current"/> is an
    /// immutable snapshot swapped atomically.
    /// </summary>
    internal sealed class SettingsStore : IDisposable
    {
        private const int DebounceMs = 500;
        private readonly McpHome _home;
        private readonly object _gate = new object();
        private volatile McpSettings _current = McpSettings.Defaults;
        private FileSystemWatcher _watcher;
        private Timer _debounce;
        private DateTime _loadedMtimeUtc = DateTime.MinValue;
        private long _loadedLength = -1;
        private DateTime _lastStatUtc = DateTime.MinValue;
        private bool _disposed;

        public SettingsStore(McpHome home)
        {
            _home = home ?? throw new ArgumentNullException(nameof(home));
        }

        /// <summary>The current settings (defaults until the file was read).</summary>
        public McpSettings Current => _current;

        /// <summary>SETTINGS_INVALID text when the file could not be parsed (last good values stay in effect).</summary>
        public string Problem { get; private set; }

        /// <summary>Non-fatal findings (clamped values, ignored keys) of the last successful load.</summary>
        public IReadOnlyList<string> Warnings { get; private set; } = new List<string>();

        /// <summary>UTC time the settings file was last loaded successfully.</summary>
        public DateTime? LoadedAtUtc { get; private set; }

        /// <summary>Last write time of the settings file at the last load (used for "code execution enabled since").</summary>
        public DateTime? FileMtimeUtc { get; private set; }

        /// <summary>Raised (on a thread-pool thread) after a reload changed the settings.</summary>
        public event Action<McpSettings> Changed;

        /// <summary>Loads the file once and starts watching it.</summary>
        public void Start()
        {
            Reload("startup");
            try
            {
                Directory.CreateDirectory(_home.ConfigDir);
                _debounce = new Timer(_ => SafeReload("watcher"), null, Timeout.Infinite, Timeout.Infinite);
                _watcher = new FileSystemWatcher(_home.ConfigDir, "settings.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
                };
                FileSystemEventHandler onChange = (sender, args) => ScheduleReload();
                _watcher.Changed += onChange;
                _watcher.Created += onChange;
                _watcher.Deleted += onChange;
                _watcher.Renamed += (sender, args) => ScheduleReload();
                _watcher.Error += (sender, args) => ScheduleReload();
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("settings", "Settings hot reload is unavailable; settings are re-read on demand. " + ex.Message);
            }
        }

        /// <summary>
        /// Returns settings that reflect the file as of now: stats the file (at most every 250 ms) and reloads
        /// synchronously when its mtime or size changed. Use for security gates (enableCodeExecution) per call.
        /// </summary>
        public McpSettings GetFresh()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastStatUtc).TotalMilliseconds < 250) return _current;
            _lastStatUtc = now;
            try
            {
                var info = new FileInfo(_home.SettingsFile);
                DateTime mtime = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                long length = info.Exists ? info.Length : -1;
                if (mtime != _loadedMtimeUtc || length != _loadedLength) Reload("on-demand");
            }
            catch
            {
                // Keep the current values.
            }
            return _current;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            try { _watcher?.Dispose(); } catch { }
            try { _debounce?.Dispose(); } catch { }
        }

        private void ScheduleReload()
        {
            try
            {
                _debounce?.Change(DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }

        private void SafeReload(string reason)
        {
            try
            {
                Reload(reason);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("Settings reload failed.", ex);
            }
        }

        private void Reload(string reason)
        {
            McpSettings changed = null;
            lock (_gate)
            {
                if (_disposed) return;
                var info = new FileInfo(_home.SettingsFile);
                if (!info.Exists)
                {
                    bool wasCustom = !ReferenceEquals(_current, McpSettings.Defaults);
                    _current = McpSettings.Defaults;
                    Problem = null;
                    Warnings = new List<string>();
                    _loadedMtimeUtc = DateTime.MinValue;
                    _loadedLength = -1;
                    FileMtimeUtc = null;
                    LoadedAtUtc = DateTime.UtcNow;
                    if (wasCustom) changed = _current;
                }
                else
                {
                    string text;
                    try
                    {
                        text = ReadShared(info.FullName);
                    }
                    catch (IOException ex)
                    {
                        // Probably mid-write; the watcher fires again when the writer closes the file.
                        DiagnosticsLogger.Info("Settings file busy (" + reason + "): " + ex.Message);
                        return;
                    }

                    _loadedMtimeUtc = info.LastWriteTimeUtc;
                    _loadedLength = info.Length;
                    FileMtimeUtc = info.LastWriteTimeUtc;
                    try
                    {
                        string trimmed = (text ?? string.Empty).Trim().TrimStart('﻿');
                        object parsed = trimmed.Length == 0 ? new Dictionary<string, object>() : JsonWireCodec.DeserializeObject(trimmed);
                        if (!(parsed is IDictionary<string, object> root))
                        {
                            throw new InvalidDataException("settings.json must contain a JSON object.");
                        }

                        var warnings = new List<string>();
                        McpSettings next = McpSettings.FromJson(root, warnings);
                        _current = next;
                        Warnings = warnings;
                        Problem = null;
                        LoadedAtUtc = DateTime.UtcNow;
                        changed = next;
                        if (warnings.Count > 0)
                        {
                            DiagnosticsLogger.Warn("settings", "settings.json: " + string.Join(" ", warnings));
                        }
                    }
                    catch (Exception ex)
                    {
                        Problem = "SETTINGS_INVALID: " + _home.SettingsFile + " could not be parsed (" + ex.Message +
                                  "); the last good settings stay in effect.";
                        DiagnosticsLogger.Warn("settings", Problem);
                    }
                }
            }

            if (changed != null)
            {
                try
                {
                    Changed?.Invoke(changed);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("A settings change handler failed.", ex);
                }
            }
        }

        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false), true))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
