using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;
#if NET5_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace RevitMcpNext.Addin.Code
{
    /// <summary>A compiled script (IL + portable PDB), cached by the sha256 of its generated source.</summary>
    internal sealed class CompiledScript
    {
        public string SourceSha { get; set; }
        public byte[] Il { get; set; }
        public byte[] Pdb { get; set; }
        public long CompileMs { get; set; }
#if !NET5_0_OR_GREATER
        /// <summary>net48 cannot unload assemblies: a cached script is loaded once and reused.</summary>
        public Assembly Loaded { get; set; }
#endif
    }

    /// <summary>How a script invocation ended.</summary>
    internal sealed class ScriptRun
    {
        /// <summary>The return value, already projected to output JSON while the script assembly was loaded.</summary>
        public object Value { get; set; }
        public Exception Error { get; set; }
        /// <summary>True when the guard stopped the script (deadline or cancel).</summary>
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        /// <summary>1-based user-code line of the failure (0 when unknown).</summary>
        public int Line { get; set; }
        public int Column { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// The add-in side of run_csharp's compiler seam (SPEC §11.7): loads RevitMcpNext.Scripting (Roslyn) lazily from
    /// &lt;payload&gt;\scripting\ — Assembly.LoadFrom plus a RevitMcpNext.*-scoped resolver on net48, a private
    /// non-collectible AssemblyLoadContext with an AssemblyDependencyResolver on net10 — and runs compiled scripts
    /// (Assembly.Load(bytes) cached by sha on net48, a collectible AssemblyLoadContext per run on net10). Nothing here is
    /// touched while code execution is disabled, so Roslyn is never loaded into Revit by default. UI thread only.
    /// </summary>
    internal static class ScriptHost
    {
        public const string ScriptingFolder = "scripting";
        public const string ScriptingAssembly = "RevitMcpNext.Scripting.dll";
        public const string CompilerType = "RevitMcpNext.Scripting.RoslynScriptCompiler";
        private const int MaxCachedScripts = 32;
        private static readonly TimeSpan LoadRetryAfter = TimeSpan.FromSeconds(30);

        private static readonly object Gate = new object();
        private static readonly LinkedList<CompiledScript> Cache = new LinkedList<CompiledScript>();
        private static readonly Dictionary<string, ScriptAnalysisResult> Analyses = new Dictionary<string, ScriptAnalysisResult>(StringComparer.Ordinal);
        private static IScriptCompiler _compiler;
        private static string _loadProblem;
        private static DateTime _loadFailedUtc = DateTime.MinValue;
        private static List<string> _references;
#if NET5_0_OR_GREATER
        private static ScriptingLoadContext _scriptingContext;
#else
        private static bool _resolverHooked;
        [ThreadStatic] private static bool _loadingScripting;
#endif

        /// <summary>&lt;payload&gt;\scripting (the add-in's own folder when no payload dir is known).</summary>
        public static string ScriptingDir
        {
            get
            {
                string payload = McpRuntime.Instance?.PayloadDir;
                if (string.IsNullOrWhiteSpace(payload))
                {
                    try { payload = Path.GetDirectoryName(typeof(ScriptHost).Assembly.Location); } catch { payload = string.Empty; }
                }
                return Path.Combine(payload ?? string.Empty, ScriptingFolder);
            }
        }

        /// <summary>Compiler identification for status/audit (null until loaded).</summary>
        public static string CompilerVersion
        {
            get { lock (Gate) return _compiler?.Version; }
        }

        /// <summary>The last load failure (null when loaded or never tried).</summary>
        public static string LoadProblem
        {
            get { lock (Gate) return _loadProblem; }
        }

        /// <summary>The compiler, loading it on first use. Throws CODE_EXECUTION_UNAVAILABLE when it cannot be loaded.</summary>
        public static IScriptCompiler Compiler()
        {
            lock (Gate)
            {
                if (_compiler != null) return _compiler;
                if (_loadProblem != null && DateTime.UtcNow - _loadFailedUtc < LoadRetryAfter) throw Unavailable(_loadProblem);
                try
                {
                    _compiler = LoadCompiler();
                    _loadProblem = null;
                    DiagnosticsLogger.Info("code", "Loaded the C# engine: " + _compiler.Version + " from " + ScriptingDir + ".");
                    return _compiler;
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    _loadProblem = inner is OpException op ? op.Message : inner.GetType().Name + ": " + inner.Message;
                    _loadFailedUtc = DateTime.UtcNow;
                    DiagnosticsLogger.Error("code", "The C# engine could not be loaded from " + ScriptingDir + ".", inner);
                    throw Unavailable(_loadProblem);
                }
            }
        }

        private static OpException Unavailable(string problem)
        {
            return new OpException(ErrorCodes.CodeExecutionUnavailable, "The C# engine could not be loaded in this Revit: " + problem,
                new Dictionary<string, object> { ["reason"] = problem, ["folder"] = ScriptingDir });
        }

        private static IScriptCompiler LoadCompiler()
        {
            string dir = ScriptingDir;
            string path = Path.Combine(dir, ScriptingAssembly);
            if (!File.Exists(path))
            {
                throw new OpException(ErrorCodes.CodeExecutionUnavailable, ScriptingAssembly + " is missing from " + dir + " (rebuild or reinstall the add-in).");
            }
            Assembly assembly;
#if NET5_0_OR_GREATER
            _scriptingContext = _scriptingContext ?? new ScriptingLoadContext(path);
            assembly = _scriptingContext.LoadFromAssemblyPath(path);
#else
            if (!_resolverHooked)
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                _resolverHooked = true;
            }
            _loadingScripting = true;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            finally
            {
                _loadingScripting = false;
            }
#endif
            Type type = assembly.GetType(CompilerType, throwOnError: true);
            object instance = Activator.CreateInstance(type);
            if (!(instance is IScriptCompiler compiler))
            {
                throw new OpException(ErrorCodes.CodeExecutionUnavailable, CompilerType + " does not implement IScriptCompiler (RevitMcpNext.Contracts loaded twice?).");
            }
            // Touch Roslyn once so a broken dependency fails here and not in the middle of a user's call.
            _ = compiler.Version;
            return compiler;
        }

        /// <summary>Framework assemblies + RevitAPI + RevitAPIUI (SPEC §11.3: scripts reference nothing else). Cached.</summary>
        public static IReadOnlyList<string> ReferencePaths()
        {
            lock (Gate)
            {
                if (_references != null) return _references;
                var list = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(string file)
                {
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
                    if (!seen.Add(Path.GetFileNameWithoutExtension(file))) return;
                    list.Add(file);
                }

                string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? RuntimeEnvironment.GetRuntimeDirectory();
#if NET5_0_OR_GREATER
                foreach (string file in Directory.GetFiles(runtimeDir, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsManaged(file)) Add(file);
                }
                // WPF/WinForms framework assemblies Revit already loaded (types in some Revit API signatures).
                string shared = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir.TrimEnd('\\')));
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string location = SafeLocation(loaded);
                    if (!string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(shared) &&
                        location.StartsWith(shared, StringComparison.OrdinalIgnoreCase)) Add(location);
                }
#else
                foreach (string name in new[] { "mscorlib", "System", "System.Core", "System.Xml", "System.Xml.Linq", "System.Data", "System.Numerics", "System.Drawing", "netstandard", "System.Runtime" })
                {
                    Add(Path.Combine(runtimeDir, name + ".dll"));
                    Add(Path.Combine(runtimeDir, "Facades", name + ".dll"));
                }
#endif
                Add(SafeLocation(typeof(Document).Assembly));
                Add(SafeLocation(typeof(UIApplication).Assembly));
                _references = list;
                return _references;
            }
        }

        /// <summary>Semantic analysis of a generated source (cached by its sha).</summary>
        public static ScriptAnalysisResult Analyze(string source, string sourceSha)
        {
            lock (Gate)
            {
                if (Analyses.TryGetValue(sourceSha, out ScriptAnalysisResult cached)) return cached;
            }
            IScriptCompiler compiler = Compiler();
            ScriptAnalysisResult result = WithScriptingResolution(() => compiler.Analyze(source, ReferencePaths(), 0));
            lock (Gate)
            {
                if (Analyses.Count >= MaxCachedScripts * 2) Analyses.Clear();
                Analyses[sourceSha] = result;
            }
            return result;
        }

        /// <summary>The compiled script for a generated source, or null with <paramref name="failure"/> set.</summary>
        public static CompiledScript GetOrCompile(string source, string sourceSha, out ScriptCompileResult failure)
        {
            failure = null;
            lock (Gate)
            {
                LinkedListNode<CompiledScript> node = Cache.First;
                while (node != null)
                {
                    if (node.Value.SourceSha == sourceSha)
                    {
                        Cache.Remove(node);
                        Cache.AddFirst(node);
                        return node.Value;
                    }
                    node = node.Next;
                }
            }

            IScriptCompiler compiler = Compiler();
            ScriptCompileResult result = WithScriptingResolution(() => compiler.Compile(source, ReferencePaths(), 0));
            if (!result.Success || result.Il == null)
            {
                failure = result;
                return null;
            }
            var script = new CompiledScript { SourceSha = sourceSha, Il = result.Il, Pdb = result.Pdb, CompileMs = result.ElapsedMs };
            lock (Gate)
            {
                Cache.AddFirst(script);
                while (Cache.Count > MaxCachedScripts) Cache.RemoveLast();
            }
            return script;
        }

        /// <summary>
        /// Runs __McpScript.Run with the guard armed (deadline, cancel polling) on the calling (UI) thread.
        /// <paramref name="project"/> converts the return value to output JSON while the script assembly is still loaded.
        /// </summary>
        public static ScriptRun Invoke(CompiledScript script, object[] arguments, TimeSpan timeout, Func<bool> cancelRequested, Func<object, object> project)
        {
            var run = new ScriptRun();
            Stopwatch watch = Stopwatch.StartNew();
#if NET5_0_OR_GREATER
            var context = new ScriptLoadContext();
            try
            {
                Assembly assembly = context.LoadFromStream(new MemoryStream(script.Il), script.Pdb == null ? null : new MemoryStream(script.Pdb));
                InvokeLoaded(assembly, arguments, timeout, cancelRequested, project, run);
            }
            finally
            {
                try { context.Unload(); } catch { }
            }
#else
            Assembly assembly;
            lock (Gate)
            {
                if (script.Loaded == null) script.Loaded = script.Pdb == null ? Assembly.Load(script.Il) : Assembly.Load(script.Il, script.Pdb);
                assembly = script.Loaded;
            }
            InvokeLoaded(assembly, arguments, timeout, cancelRequested, project, run);
#endif
            run.DurationMs = watch.ElapsedMilliseconds;
            return run;
        }

        private static void InvokeLoaded(Assembly assembly, object[] arguments, TimeSpan timeout, Func<bool> cancelRequested, Func<object, object> project, ScriptRun run)
        {
            Type scriptType = assembly.GetType(ScriptSource.ScriptType, throwOnError: true);
            Type guardType = assembly.GetType(ScriptSource.GuardType, throwOnError: true);
            MethodInfo method = scriptType.GetMethod(ScriptSource.RunMethod, BindingFlags.Public | BindingFlags.Static)
                                ?? throw new InvalidOperationException("The generated script has no Run method.");
            FieldInfo deadline = guardType.GetField("DeadlineTicks", BindingFlags.Public | BindingFlags.Static);
            FieldInfo cancel = guardType.GetField("Cancel", BindingFlags.Public | BindingFlags.Static);
            FieldInfo depth = guardType.GetField("Depth", BindingFlags.Public | BindingFlags.Static);
            FieldInfo maxDepth = guardType.GetField("MaxDepth", BindingFlags.Public | BindingFlags.Static);
            deadline?.SetValue(null, DateTime.UtcNow.Add(timeout).Ticks);
            cancel?.SetValue(null, false);
            depth?.SetValue(null, 0);
            maxDepth?.SetValue(null, ScriptSource.MaxDepth);

            bool cancelled = false;
            Timer poll = null;
            if (cancelRequested != null)
            {
                poll = new Timer(_ =>
                {
                    try
                    {
                        if (!cancelled && cancelRequested())
                        {
                            cancelled = true;
                            cancel?.SetValue(null, true);
                        }
                    }
                    catch
                    {
                        // Polling is best effort.
                    }
                }, null, 200, 200);
            }

            try
            {
                object value = method.Invoke(null, arguments);
                run.Value = project == null ? value : project(value);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Classify(ex.InnerException, run);
            }
            catch (Exception ex)
            {
                Classify(ex, run);
            }
            finally
            {
                poll?.Dispose();
                deadline?.SetValue(null, 0L);
            }
            run.Cancelled = run.Cancelled || (cancelled && run.TimedOut);
        }

        private static void Classify(Exception error, ScriptRun run)
        {
            run.Error = error;
            if (error.GetType().Name == ScriptSource.TimeoutType)
            {
                run.TimedOut = true;
                run.Cancelled = error.Message.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            UserFrame(error, out int line, out int column);
            run.Line = line;
            run.Column = column;
        }

        /// <summary>The innermost stack frame in user code ("user.cs" through the #line directive and the portable PDB).</summary>
        internal static void UserFrame(Exception error, out int line, out int column)
        {
            line = 0;
            column = 0;
            try
            {
                var trace = new StackTrace(error, true);
                foreach (StackFrame frame in trace.GetFrames() ?? new StackFrame[0])
                {
                    string file = frame.GetFileName();
                    if (file == null || !file.EndsWith("user.cs", StringComparison.OrdinalIgnoreCase)) continue;
                    line = frame.GetFileLineNumber();
                    column = frame.GetFileColumnNumber();
                    return;
                }
            }
            catch
            {
                // Line info is a courtesy.
            }
        }

        /// <summary>Clears compiled and analysed scripts (they are rebuilt on demand).</summary>
        public static void ClearCache()
        {
            lock (Gate)
            {
                Cache.Clear();
                Analyses.Clear();
            }
        }

        private static T WithScriptingResolution<T>(Func<T> action)
        {
#if NET5_0_OR_GREATER
            return action();
#else
            bool previous = _loadingScripting;
            _loadingScripting = true;
            try
            {
                return action();
            }
            finally
            {
                _loadingScripting = previous;
            }
#endif
        }

        private static string SafeLocation(Assembly assembly)
        {
            try
            {
                return assembly.IsDynamic ? null : assembly.Location;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsManaged(string file)
        {
            try
            {
                AssemblyName.GetAssemblyName(file);
                return true;
            }
            catch
            {
                return false;
            }
        }

#if NET5_0_OR_GREATER
        /// <summary>Roslyn's private, non-collectible context: RevitMcpNext.* from the add-in's context, Roslyn from scripting\.</summary>
        private sealed class ScriptingLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;
            private readonly AssemblyLoadContext _host;

            public ScriptingLoadContext(string scriptingAssemblyPath)
                : base("RevitMcpNext.Scripting", isCollectible: false)
            {
                _resolver = new AssemblyDependencyResolver(scriptingAssemblyPath);
                _host = GetLoadContext(typeof(ScriptHost).Assembly) ?? Default;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                string name = assemblyName.Name ?? string.Empty;
                if (name.StartsWith("RevitMcpNext.", StringComparison.Ordinal) && !name.Equals("RevitMcpNext.Scripting", StringComparison.Ordinal))
                {
                    return _host.LoadFromAssemblyName(assemblyName);
                }
                string path = _resolver.ResolveAssemblyToPath(assemblyName);
                return path == null ? null : LoadFromAssemblyPath(path);
            }
        }

        /// <summary>One collectible context per script run; framework and Revit assemblies come from the default context.</summary>
        private sealed class ScriptLoadContext : AssemblyLoadContext
        {
            public ScriptLoadContext()
                : base("RevitMcpNext.Script", isCollectible: true)
            {
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                return null;
            }
        }
#else
        /// <summary>
        /// RevitMcpNext.* → the already loaded assembly (the scripting folder has no Contracts copy). While Roslyn loads or
        /// runs, its own dependencies resolve version-agnostically from the scripting folder (a binding redirect scoped to
        /// that folder, since Revit.exe.config has none for them).
        /// </summary>
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string simple = new AssemblyName(args.Name).Name ?? string.Empty;
                if (simple.StartsWith("RevitMcpNext.", StringComparison.Ordinal))
                {
                    return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, simple, StringComparison.Ordinal));
                }
                string dir = ScriptingDir;
                bool fromScripting = args.RequestingAssembly != null && IsUnder(SafeLocation(args.RequestingAssembly), dir);
                if (!fromScripting && !_loadingScripting) return null;
                string candidate = Path.Combine(dir, simple + ".dll");
                if (!File.Exists(candidate)) return null;
                Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && IsUnder(SafeLocation(a), dir) &&
                    string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase));
                return loaded ?? Assembly.LoadFrom(candidate);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("code", "Assembly resolution for " + args.Name + " failed: " + ex.Message);
                return null;
            }
        }

        private static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
#endif
    }
}
