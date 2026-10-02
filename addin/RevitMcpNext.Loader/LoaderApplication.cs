using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace RevitMcpNext.Loader
{
    /// <summary>
    /// Loads the Revit MCP Next payload of the runtime home this Revit process uses (SPEC §9.2):
    /// 1. home = env REVIT_MCP_NEXT_HOME, else the home this DLL lives in (&lt;home&gt;\addin\&lt;year&gt;\loader\&lt;ver&gt;\, marker
    ///    checked), else %USERPROFILE%\.revit-mcp-next;
    /// 2. &lt;home&gt;\addin\&lt;year&gt;\current.json names the payload; REVIT_MCP_NEXT_HOME and REVIT_MCP_NEXT_PAYLOAD_DIR are set
    ///    in-process;
    /// 3. RevitMcpNext.* assemblies resolve from the payload folder;
    /// 4. RevitMcpNext.Addin.RevitMcpApplication is created and OnStartup/OnShutdown are forwarded.
    /// Every failure is written to &lt;home&gt;\logs\loader-&lt;year&gt;.log and Result.Succeeded is returned: the loader never
    /// shows a dialog and never blocks Revit.
    /// </summary>
    public sealed class LoaderApplication : IExternalApplication
    {
        private const string HomeVariable = "REVIT_MCP_NEXT_HOME";
        private const string PayloadVariable = "REVIT_MCP_NEXT_PAYLOAD_DIR";
        private const string MarkerFileName = ".revit-mcp-next-home";
        private const string PayloadAssembly = "RevitMcpNext.Addin.dll";
        private const string PayloadType = "RevitMcpNext.Addin.RevitMcpApplication";
        private static readonly Regex PayloadIdPattern = new Regex("\"payloadId\"\\s*:\\s*\"(?<id>[A-Za-z0-9._-]{1,64})\"", RegexOptions.CultureInvariant);

        private static string _payloadDir;
        private static string _logPath;
        private static bool _resolverRegistered;
        private IExternalApplication _payload;

        public Result OnStartup(UIControlledApplication application)
        {
            int year = RevitYear();
            string home = null;
            try
            {
                home = ResolveHome(year, out string source);
                _logPath = Path.Combine(home, "logs", "loader-" + year.ToString(CultureInfo.InvariantCulture) + ".log");
                Log("loader " + LoaderVersion() + " starting in Revit " + year.ToString(CultureInfo.InvariantCulture) + "; home " + home + " [" + source + "]");

                string addinDir = Path.Combine(home, "addin", year.ToString(CultureInfo.InvariantCulture));
                string currentJson = Path.Combine(addinDir, "current.json");
                if (!File.Exists(currentJson))
                {
                    Log("no payload installed for Revit " + year.ToString(CultureInfo.InvariantCulture) + ": " + currentJson + " is missing (run scripts/dev-install.ps1 or the installer)");
                    return Result.Succeeded;
                }

                Match match = PayloadIdPattern.Match(File.ReadAllText(currentJson, Encoding.UTF8));
                if (!match.Success)
                {
                    Log("current.json has no valid payloadId: " + currentJson);
                    return Result.Succeeded;
                }
                string payloadId = match.Groups["id"].Value;
                _payloadDir = Path.Combine(addinDir, payloadId);
                string payloadDll = Path.Combine(_payloadDir, PayloadAssembly);
                if (!File.Exists(payloadDll))
                {
                    Log("payload " + payloadId + " is missing " + payloadDll);
                    return Result.Succeeded;
                }

                Environment.SetEnvironmentVariable(HomeVariable, home);
                Environment.SetEnvironmentVariable(PayloadVariable, _payloadDir);
                RegisterResolver();

                Assembly payload = LoadPayload(payloadDll);
                Type type = payload.GetType(PayloadType, true);
                _payload = (IExternalApplication)Activator.CreateInstance(type);
                Result result = _payload.OnStartup(application);
                Log("payload " + payloadId + " (" + PayloadVersion(payload) + ") OnStartup returned " + result);
            }
            catch (Exception ex)
            {
                Log("loader failed: " + ex);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                _payload?.OnShutdown(application);
                Log("payload shut down");
            }
            catch (Exception ex)
            {
                Log("payload OnShutdown failed: " + ex);
            }
            return Result.Succeeded;
        }

        /// <summary>env → the home this loader is installed in (marker checked) → %USERPROFILE%\.revit-mcp-next.</summary>
        private static string ResolveHome(int year, out string source)
        {
            string fromEnv = Environment.GetEnvironmentVariable(HomeVariable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                source = "env";
                return Path.GetFullPath(fromEnv.Trim().Trim('"'));
            }

            try
            {
                string directory = Path.GetDirectoryName(typeof(LoaderApplication).Assembly.Location);
                for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
                {
                    if (File.Exists(Path.Combine(directory, MarkerFileName)))
                    {
                        source = "self";
                        return directory;
                    }
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch
            {
                // Fall back to the default home.
            }

            source = "default";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-mcp-next");
        }

        private static Assembly LoadPayload(string payloadDll)
        {
#if NETFRAMEWORK
            return Assembly.LoadFrom(payloadDll);
#else
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(LoaderApplication).Assembly) ?? AssemblyLoadContext.Default;
            return context.LoadFromAssemblyPath(payloadDll);
#endif
        }

        private static void RegisterResolver()
        {
            if (_resolverRegistered) return;
            _resolverRegistered = true;
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => Resolve(new AssemblyName(args.Name), null);
#if !NETFRAMEWORK
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(LoaderApplication).Assembly);
            if (context != null) context.Resolving += (alc, name) => Resolve(name, alc);
#endif
        }

        /// <summary>Resolves RevitMcpNext.* assemblies from the payload folder (already loaded copies first).</summary>
#if NETFRAMEWORK
        private static Assembly Resolve(AssemblyName name, object unused)
#else
        private static Assembly Resolve(AssemblyName name, AssemblyLoadContext context)
#endif
        {
            try
            {
                if (name?.Name == null || !name.Name.StartsWith("RevitMcpNext.", StringComparison.Ordinal) || string.IsNullOrEmpty(_payloadDir)) return null;
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AssemblyName loadedName = loaded.GetName();
                    if (string.Equals(loadedName.Name, name.Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(SafeLocation(loaded)) &&
                        SafeLocation(loaded).StartsWith(_payloadDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return loaded;
                    }
                }
                string candidate = Path.Combine(_payloadDir, name.Name + ".dll");
                if (!File.Exists(candidate)) return null;
#if NETFRAMEWORK
                return Assembly.LoadFrom(candidate);
#else
                return (context ?? AssemblyLoadContext.GetLoadContext(typeof(LoaderApplication).Assembly) ?? AssemblyLoadContext.Default).LoadFromAssemblyPath(candidate);
#endif
            }
            catch (Exception ex)
            {
                Log("resolving " + name + " failed: " + ex.Message);
                return null;
            }
        }

        private static int RevitYear()
        {
#if REVIT2027
            const int compiled = 2027;
#else
            const int compiled = 2024;
#endif
            try
            {
                int major = typeof(UIControlledApplication).Assembly.GetName().Version.Major;
                int runtime = major < 100 ? 2000 + major : major;
                return runtime >= 2020 && runtime <= 2100 ? runtime : compiled;
            }
            catch
            {
                return compiled;
            }
        }

        private static string LoaderVersion()
        {
            try
            {
                return typeof(LoaderApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                       typeof(LoaderApplication).Assembly.GetName().Version?.ToString() ?? "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string PayloadVersion(Assembly payload)
        {
            try
            {
                return payload.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? payload.GetName().Version?.ToString() ?? "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string SafeLocation(Assembly assembly)
        {
            try { return assembly.Location ?? string.Empty; } catch { return string.Empty; }
        }

        private static void Log(string message)
        {
            try
            {
                string path = _logPath ?? Path.Combine(Path.GetTempPath(), "revit-mcp-next-loader.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // The loader must never affect Revit.
            }
        }
    }
}
