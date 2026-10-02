using System;
using System.Globalization;
using System.IO;
using System.Text;
using RevitMcpNext.Addin.Ipc;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The runtime home (SPEC §3): env REVIT_MCP_NEXT_HOME ?? self-located home (marker next to the payload/loader)
    /// ?? %USERPROFILE%\.revit-mcp-next. Every path the add-in writes is built here; mirrors contracts/src/home.ts.
    /// </summary>
    internal sealed class McpHome
    {
        public const string HomeEnvironmentVariable = "REVIT_MCP_NEXT_HOME";
        public const string PayloadDirEnvironmentVariable = "REVIT_MCP_NEXT_PAYLOAD_DIR";
        public const string MarkerFileName = ".revit-mcp-next-home";
        public const string DefaultHomeFolderName = ".revit-mcp-next";

        private McpHome(string root, string source)
        {
            Root = root;
            Source = source;
        }

        /// <summary>Absolute home directory.</summary>
        public string Root { get; }

        /// <summary>Which resolution step won: env | self | default.</summary>
        public string Source { get; }

        /// <summary>%USERPROFILE%\.revit-mcp-next (the installed home).</summary>
        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultHomeFolderName);

        /// <summary>True when this is the user's installed home (test ops are never registered there).</summary>
        public bool IsDefaultHome => PathsEqual(Root, DefaultRoot);

        /// <summary>Non-null when the home sits in a location the installer refuses (AppData, Temp, MSIX packages).</summary>
        public string LocationWarning { get; private set; }

        public string MarkerFile => Path.Combine(Root, MarkerFileName);
        public string InstallReceipt => Path.Combine(Root, "install.json");
        public string ConfigDir => Path.Combine(Root, "config");
        public string AuthFile => Path.Combine(ConfigDir, "auth.env");
        public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
        public string TestOpsEnableFile => Path.Combine(ConfigDir, "e2e-test-ops.enable");
        public string InstancesDir => Path.Combine(Root, "instances");
        public string LedgerDir => Path.Combine(Root, "ledger");
        public string JobsRootDir => Path.Combine(Root, "jobs");
        public string StateDir => Path.Combine(Root, "state");
        public string InflightDir => Path.Combine(StateDir, "inflight");
        public string LogsDir => Path.Combine(Root, "logs");
        public string CodeLogDir => Path.Combine(LogsDir, "code");
        public string CodeAuditLog => Path.Combine(LogsDir, "code-exec-audit.jsonl");
        public string CapturesDir => Path.Combine(Root, "captures");
        public string ExportsDir => Path.Combine(Root, "exports");
        public string RecipesDir => Path.Combine(Root, "recipes");
        public string PluginsDir => Path.Combine(Root, "plugins");
        public string RuntimeDir => Path.Combine(Root, "runtime");
        public string BrokerDir => Path.Combine(Root, "broker");
        public string RunsDir => Path.Combine(Root, "runs");

        public string InstanceFile(string instanceId) => Path.Combine(InstancesDir, instanceId + ".json");
        public string LockFile(string instanceId) => Path.Combine(InstancesDir, instanceId + ".lock");
        public string LedgerFile(string instanceId) => Path.Combine(LedgerDir, instanceId + ".jsonl");
        public string JobsDir(string instanceId) => Path.Combine(JobsRootDir, instanceId);
        public string JobFile(string instanceId, string jobId) => Path.Combine(JobsDir(instanceId), jobId + ".json");
        public string CodeSourceFile(string sha256) => Path.Combine(CodeLogDir, sha256 + ".cs");
        public string AddinDir(int year) => Path.Combine(Root, "addin", year.ToString(CultureInfo.InvariantCulture));
        public string AddinCurrentJson(int year) => Path.Combine(AddinDir(year), "current.json");
        public string PayloadDir(int year, string payloadId) => Path.Combine(AddinDir(year), payloadId);
        public string LoaderDir(int year, string loaderVersion) => Path.Combine(AddinDir(year), "loader", loaderVersion);

        /// <summary>logs\addin-&lt;year&gt;-YYYYMMDD.&lt;n&gt;.jsonl</summary>
        public string AddinLogFile(int year, DateTime utcDate, int index) =>
            Path.Combine(LogsDir, "addin-" + year.ToString(CultureInfo.InvariantCulture) + "-" +
                utcDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "." + index.ToString(CultureInfo.InvariantCulture) + ".jsonl");

        public string LoaderLogFile(int year) => Path.Combine(LogsDir, "loader-" + year.ToString(CultureInfo.InvariantCulture) + ".log");

        /// <summary>captures\yyyy-MM-dd\HHmmss-&lt;rand6&gt;\ (a fresh folder per capture).</summary>
        public string CaptureDir(DateTime utc, string stamp) =>
            Path.Combine(CapturesDir, utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), stamp);

        /// <summary>exports\&lt;docTitle&gt;\ with the title made file-name safe.</summary>
        public string ExportDir(string docTitle) => Path.Combine(ExportsDir, SafeFileName(docTitle));

        /// <summary>Resolves the home for this process (does not create anything).</summary>
        public static McpHome Resolve()
        {
            string fromEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Create(fromEnv.Trim().Trim('"'), "env");
            }

            string selfLocated = SelfLocate(typeof(McpHome).Assembly.Location);
            if (selfLocated != null)
            {
                return Create(selfLocated, "self");
            }

            return Create(DefaultRoot, "default");
        }

        /// <summary>Walks up from a file path looking for the home marker (at most 6 levels).</summary>
        public static string SelfLocate(string startPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(startPath)) return null;
                string directory = Path.GetDirectoryName(Path.GetFullPath(startPath));
                for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
                {
                    if (File.Exists(Path.Combine(directory, MarkerFileName))) return directory;
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch
            {
                // Self-location is best effort; fall back to the default home.
            }

            return null;
        }

        private static McpHome Create(string root, string source)
        {
            string full;
            try
            {
                full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                full = root;
            }

            var home = new McpHome(full, source);
            home.LocationWarning = DescribeForbiddenLocation(full);
            return home;
        }

        /// <summary>
        /// Creates the standard directories and the marker when missing. Never throws: failures are returned as text
        /// so the add-in can keep running and report the problem.
        /// </summary>
        public string EnsureLayout()
        {
            var problems = new StringBuilder();
            foreach (string directory in new[]
            {
                Root, ConfigDir, InstancesDir, LedgerDir, JobsRootDir, InflightDir, LogsDir, CapturesDir, ExportsDir, RecipesDir
            })
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception ex)
                {
                    problems.Append("cannot create ").Append(directory).Append(": ").Append(ex.Message).Append("; ");
                }
            }

            try
            {
                if (!File.Exists(MarkerFile))
                {
                    string json = JsonWireCodec.Serialize(new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["schema"] = 1,
                        ["createdAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        ["createdBy"] = "addin"
                    });
                    AtomicFile.WriteAllTextIfMissing(MarkerFile, json);
                }
            }
            catch (Exception ex)
            {
                problems.Append("cannot write ").Append(MarkerFile).Append(": ").Append(ex.Message).Append("; ");
            }

            return problems.Length == 0 ? null : problems.ToString().TrimEnd(' ', ';');
        }

        public static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd('\\', '/'),
                    Path.GetFullPath(right).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Replaces characters that are invalid in file names; never returns an empty name.</summary>
        public static string SafeFileName(string name)
        {
            string text = string.IsNullOrWhiteSpace(name) ? "untitled" : name.Trim();
            var builder = new StringBuilder(text.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char character in text)
            {
                builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            }
            string safe = builder.ToString().Trim('.', ' ');
            if (safe.Length > 120) safe = safe.Substring(0, 120);
            return safe.Length == 0 ? "untitled" : safe;
        }

        private static string DescribeForbiddenLocation(string root)
        {
            try
            {
                string[] forbidden =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Path.GetTempPath()
                };
                foreach (string prefix in forbidden)
                {
                    if (string.IsNullOrWhiteSpace(prefix)) continue;
                    string normalized = Path.GetFullPath(prefix).TrimEnd('\\') + "\\";
                    if ((root + "\\").StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return "The home " + root + " is under " + prefix.TrimEnd('\\') +
                               ", which MSIX-packaged clients may not see. Use %USERPROFILE%\\.revit-mcp-next.";
                    }
                }

                if (root.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    root.IndexOf("\\Packages\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "The home " + root + " is inside an app package folder. Use %USERPROFILE%\\.revit-mcp-next.";
                }
            }
            catch
            {
                // Location checks are advisory only.
            }

            return null;
        }
    }

    /// <summary>Atomic file writes shared by the registration, ledger, jobs and settings code.</summary>
    internal static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Writes text atomically: tmp file (write-through) then File.Replace (3 retries at 50/100/200 ms) or File.Move.
        /// Never falls back to a non-atomic copy; on failure the old file is kept and false is returned.
        /// </summary>
        public static bool WriteAllText(string path, string text, out string error)
        {
            error = null;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = Utf8NoBom.GetBytes(text ?? string.Empty);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                int[] delays = { 50, 100, 200 };
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(temporary, path, null, true);
                        }
                        else
                        {
                            File.Move(temporary, path);
                        }
                        return true;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < delays.Length)
                    {
                        System.Threading.Thread.Sleep(delays[attempt]);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // A stale .tmp file is ignored by readers.
                }
            }
        }

        /// <summary>Creates the file only when it does not exist (first writer wins); returns false when it existed.</summary>
        public static bool WriteAllTextIfMissing(string path, string text)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(path)) return false;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text ?? string.Empty, Utf8NoBom);
                try
                {
                    File.Move(temporary, path);
                    return true;
                }
                catch (IOException) when (File.Exists(path))
                {
                    return false;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }
}
