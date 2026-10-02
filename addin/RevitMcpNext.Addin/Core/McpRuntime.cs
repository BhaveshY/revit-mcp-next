using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>Identity of this Revit process's add-in instance (hello, snapshot, registration).</summary>
    internal sealed class InstanceIdentity
    {
        /// <summary>r&lt;year&gt;-&lt;pid&gt;-&lt;6 hex&gt;.</summary>
        public string InstanceId { get; set; } = string.Empty;
        public int Pid { get; set; }
        public int Year { get; set; }
        public string Build { get; set; } = string.Empty;
        /// <summary>Three-letter Revit UI language (ENU, DEU, ...).</summary>
        public string Language { get; set; } = string.Empty;
        /// <summary>Product version without the build metadata, e.g. 0.4.0.</summary>
        public string AddinVersion { get; set; } = string.Empty;
        /// <summary>Short git sha the payload was built from (from AssemblyInformationalVersion).</summary>
        public string GitSha { get; set; } = string.Empty;
        public string PayloadId { get; set; } = string.Empty;
        public string PayloadDir { get; set; } = string.Empty;
        public string CatalogHash { get; set; } = string.Empty;
        public string PipeName { get; set; } = string.Empty;
        public string ControlPipeName { get; set; } = string.Empty;
        public string HomeRoot { get; set; } = string.Empty;

        /// <summary>Builds the identity once at startup (the payload is hashed never: payloadId comes from its folder).</summary>
        public static InstanceIdentity Create(ControlledApplication app, McpHome home, string catalogHash)
        {
            int year = 0;
            int.TryParse(app?.VersionNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
            if (year == 0) year = RevitYearFromAssembly();
            int pid = Process.GetCurrentProcess().Id;
            string instanceId = PipeNameProvider.CreateInstanceId(year, pid);
            ReadVersion(out string version, out string sha);
            string payloadDir = ResolvePayloadDir();
            return new InstanceIdentity
            {
                InstanceId = instanceId,
                Pid = pid,
                Year = year,
                Build = SafeBuild(app),
                Language = LanguageCode(app),
                AddinVersion = version,
                GitSha = sha,
                PayloadDir = payloadDir,
                PayloadId = ResolvePayloadId(payloadDir, home, year),
                CatalogHash = catalogHash ?? string.Empty,
                PipeName = BridgeProtocol.PrimaryPipeName(instanceId),
                ControlPipeName = BridgeProtocol.ControlPipeName(instanceId),
                HomeRoot = home?.Root ?? string.Empty
            };
        }

        public static void ReadVersion(out string version, out string sha)
        {
            version = "0.0.0";
            sha = string.Empty;
            try
            {
                Assembly assembly = typeof(InstanceIdentity).Assembly;
                string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrWhiteSpace(informational))
                {
                    version = assembly.GetName().Version?.ToString(3) ?? version;
                    return;
                }
                int plus = informational.IndexOf('+');
                version = plus < 0 ? informational : informational.Substring(0, plus);
                sha = plus < 0 ? string.Empty : informational.Substring(plus + 1);
            }
            catch
            {
                // Keep the defaults.
            }
        }

        private static string ResolvePayloadDir()
        {
            string fromEnv = Environment.GetEnvironmentVariable(McpHome.PayloadDirEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            try
            {
                return Path.GetDirectoryName(typeof(InstanceIdentity).Assembly.Location) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolvePayloadId(string payloadDir, McpHome home, int year)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(payloadDir) && home != null)
                {
                    string parent = Path.GetDirectoryName(Path.GetFullPath(payloadDir).TrimEnd('\\'));
                    if (McpHome.PathsEqual(parent, home.AddinDir(year))) return Path.GetFileName(Path.GetFullPath(payloadDir).TrimEnd('\\'));
                }
            }
            catch
            {
                // Not an installed payload.
            }
            return "dev";
        }

        private static string SafeBuild(ControlledApplication app)
        {
            try { return app?.VersionBuild ?? string.Empty; } catch { return string.Empty; }
        }

        private static int RevitYearFromAssembly()
        {
            try
            {
                int major = typeof(Autodesk.Revit.DB.Document).Assembly.GetName().Version.Major;
                return major < 100 ? 2000 + major : major;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Maps LanguageType to Autodesk's three-letter codes (ENU, DEU, ...).</summary>
        public static string LanguageCode(ControlledApplication app)
        {
            string name;
            try
            {
                name = app?.Language.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }

            switch (name)
            {
                case "English_USA": return "ENU";
                case "English_GB": return "ENG";
                case "German": return "DEU";
                case "French": return "FRA";
                case "Italian": return "ITA";
                case "Spanish": return "ESP";
                case "Japanese": return "JPN";
                case "Korean": return "KOR";
                case "Chinese_Simplified": return "CHS";
                case "Chinese_Traditional": return "CHT";
                case "Russian": return "RUS";
                case "Czech": return "CSY";
                case "Polish": return "PLK";
                case "Hungarian": return "HUN";
                case "Brazilian_Portuguese": return "PTB";
                case "Dutch": return "NLD";
                default: return name;
            }
        }
    }

    /// <summary>
    /// Process-wide services, created once by RevitMcpApplication.OnStartup (UI thread) before the pipe host starts.
    /// Handlers normally use the same services through RequestContext; this hub is for code without a context
    /// (event handlers, the in-process bridge, static helpers).
    /// </summary>
    internal static class McpRuntime
    {
        public static McpHome Home { get; internal set; }
        public static SettingsStore Settings { get; internal set; }
        public static AuthTokenStore Auth { get; internal set; }
        public static InstanceIdentity Instance { get; internal set; }
        public static DocumentRegistry Registry { get; internal set; }
        public static RevitRequestQueue Queue { get; internal set; }
        public static JobRunner Jobs { get; internal set; }
        public static OperationRegistry Ops { get; internal set; }
        public static RequestOutcomeLedger Ledger { get; internal set; }
        public static ExternalEventPump Pump { get; internal set; }
        public static DialogPolicy Dialogs { get; internal set; }
        public static UiStateMonitor UiMonitor { get; internal set; }

        /// <summary>Current settings (defaults before startup).</summary>
        public static McpSettings CurrentSettings => Settings?.Current ?? McpSettings.Defaults;

        /// <summary>
        /// True when the e2e-only test.* ops may be registered: the home is not the user's installed home and
        /// &lt;home&gt;\config\e2e-test-ops.enable exists (SPEC §13.4).
        /// </summary>
        public static bool TestOpsEnabled
        {
            get
            {
                try
                {
                    return Home != null && !Home.IsDefaultHome && File.Exists(Home.TestOpsEnableFile);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>codeExecution.e2ePreapproved, honoured only under the <see cref="TestOpsEnabled"/> conditions.</summary>
        public static bool CodeExecutionPreapproved =>
            TestOpsEnabled && (Settings?.GetFresh().CodeExecution.E2ePreapproved ?? false);
    }
}
