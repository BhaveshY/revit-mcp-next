using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Scripting
{
    /// <summary>
    /// The Roslyn implementation of <see cref="IScriptCompiler"/> (SPEC §11.3-§11.7). The add-in generates the script
    /// source (the user's method body wrapped in <c>__McpScript.Run</c>, plus the generated <c>__McpGuard</c> class) and
    /// passes the reference paths; this class
    /// <list type="bullet">
    /// <item>analyzes it semantically against the deny lists (<see cref="ScriptAnalyzer"/>), and</item>
    /// <item>compiles it with loop/recursion guards inserted by <see cref="GuardRewriter"/> into an in-memory assembly
    /// with a portable PDB (runtime errors then carry user-code line numbers).</item>
    /// </list>
    /// Diagnostics are mapped through the <c>#line</c> directives of the generated source, so lines and columns refer to
    /// the user's code. Instances are thread-safe; metadata references are cached per path.
    /// </summary>
    public sealed class RoslynScriptCompiler : IScriptCompiler
    {
        /// <summary>The document name the generated source uses in its <c>#line</c> directive for user code.</summary>
        public const string UserDocument = "user.cs";

        private const int MaxDiagnostics = 20;

        private readonly object _gate = new object();
        private readonly Dictionary<string, MetadataReference> _references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        public RoslynScriptCompiler()
        {
        }

        public string Version
        {
            get
            {
                Assembly roslyn = typeof(CSharpCompilation).Assembly;
                string file = roslyn.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                              roslyn.GetName().Version?.ToString() ?? "?";
                int plus = file.IndexOf('+');
                return "Microsoft.CodeAnalysis.CSharp " + (plus > 0 ? file.Substring(0, plus) : file);
            }
        }

        public ScriptAnalysisResult Analyze(string source, IReadOnlyList<string> referencePaths, int langVersion)
        {
            var result = new ScriptAnalysisResult();
            CSharpCompilation compilation = CreateCompilation(source, referencePaths, langVersion, "McpScriptAnalysis", out SyntaxTree tree);
            result.Diagnostics.AddRange(MapDiagnostics(compilation.GetDiagnostics(), errorsOnly: true));
            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
            var analyzer = new ScriptAnalyzer(model);
            analyzer.Visit(tree.GetRoot());
            result.DeniedSymbols.AddRange(analyzer.Denied);
            result.UsesTransactions = analyzer.UsesTransactions;
            return result;
        }

        public ScriptCompileResult Compile(string source, IReadOnlyList<string> referencePaths, int langVersion)
        {
            Stopwatch watch = Stopwatch.StartNew();
            var result = new ScriptCompileResult();
            string assemblyName = "McpScript_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            CSharpCompilation compilation = CreateCompilation(source, referencePaths, langVersion, assemblyName, out SyntaxTree tree);

            // 1. The user's code as written: compile errors map to their lines.
            List<ScriptDiagnostic> errors = MapDiagnostics(compilation.GetDiagnostics(), errorsOnly: true);
            if (errors.Count > 0)
            {
                result.Success = false;
                result.Diagnostics.AddRange(errors);
                result.ElapsedMs = watch.ElapsedMilliseconds;
                return result;
            }

            // 2. Insert the loop/recursion guards (needs the semantic model of the original tree for lambdas).
            SemanticModel model = compilation.GetSemanticModel(tree);
            var rewriter = new GuardRewriter(model);
            SyntaxNode guarded = rewriter.Visit(tree.GetRoot());
            SyntaxTree guardedTree = tree.WithRootAndOptions(guarded, tree.Options);
            CSharpCompilation final = compilation.ReplaceSyntaxTree(tree, guardedTree);

            // 3. Emit IL + portable PDB.
            using (var il = new MemoryStream())
            using (var pdb = new MemoryStream())
            {
                EmitResult emit = final.Emit(il, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
                result.Diagnostics.AddRange(MapDiagnostics(emit.Diagnostics, errorsOnly: !emit.Success, includeWarnings: emit.Success));
                result.Success = emit.Success;
                if (emit.Success)
                {
                    result.Il = il.ToArray();
                    result.Pdb = pdb.ToArray();
                }
            }
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        private CSharpCompilation CreateCompilation(string source, IReadOnlyList<string> referencePaths, int langVersion, string assemblyName, out SyntaxTree tree)
        {
            var parse = new CSharpParseOptions(ToLanguageVersion(langVersion), DocumentationMode.None, SourceCodeKind.Regular);
            // An encoding is required to emit debug information (portable PDB) for the text.
            tree = CSharpSyntaxTree.ParseText(SourceText.From(source ?? string.Empty, Encoding.UTF8), parse, path: "script.cs");
            var options = new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    allowUnsafe: false,
                    platform: Platform.AnyCpu,
                    warningLevel: 4,
                    deterministic: true,
                    concurrentBuild: false,
                    nullableContextOptions: NullableContextOptions.Disable)
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                // Unreachable code (our trailing "return null;") and unused variables are not worth reporting.
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                {
                    ["CS0162"] = ReportDiagnostic.Suppress,
                    ["CS0168"] = ReportDiagnostic.Suppress,
                    ["CS0219"] = ReportDiagnostic.Suppress,
                    ["CS8321"] = ReportDiagnostic.Suppress
                });
            return CSharpCompilation.Create(assemblyName, new[] { tree }, References(referencePaths), options);
        }

        private List<MetadataReference> References(IReadOnlyList<string> paths)
        {
            var list = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (string path in paths ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    // One reference per simple name (the first wins): duplicate identities would make every type ambiguous.
                    string simple = Path.GetFileNameWithoutExtension(path);
                    if (!seen.Add(simple)) continue;
                    if (!_references.TryGetValue(path, out MetadataReference reference))
                    {
                        if (!File.Exists(path)) continue;
                        try
                        {
                            reference = MetadataReference.CreateFromFile(path);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        _references[path] = reference;
                    }
                    list.Add(reference);
                }
            }
            return list;
        }

        private static LanguageVersion ToLanguageVersion(int value)
        {
            switch (value)
            {
                case 0: return LanguageVersion.Latest;
                case 7: return LanguageVersion.CSharp7_3;
                case 8: return LanguageVersion.CSharp8;
                case 9: return LanguageVersion.CSharp9;
                case 10: return LanguageVersion.CSharp10;
                case 11: return LanguageVersion.CSharp11;
                default: return LanguageVersion.Latest;
            }
        }

        /// <summary>Maps Roslyn diagnostics to user-code positions (generated code → line 0).</summary>
        internal static List<ScriptDiagnostic> MapDiagnostics(IEnumerable<Diagnostic> diagnostics, bool errorsOnly, bool includeWarnings = false)
        {
            var list = new List<ScriptDiagnostic>();
            foreach (Diagnostic diagnostic in diagnostics.OrderBy(d => d.Location.SourceSpan.Start))
            {
                bool error = diagnostic.Severity == DiagnosticSeverity.Error;
                bool warning = diagnostic.Severity == DiagnosticSeverity.Warning;
                if (!error && !(includeWarnings && warning)) continue;
                if (errorsOnly && !error) continue;
                Position(diagnostic.Location, out int line, out int column);
                list.Add(new ScriptDiagnostic
                {
                    Id = diagnostic.Id,
                    Severity = error ? "error" : "warning",
                    Message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                    Line = line,
                    Column = column
                });
                if (list.Count >= MaxDiagnostics) break;
            }
            return list;
        }

        /// <summary>1-based user-code line/column of a location; 0/0 for generated code or no source.</summary>
        internal static void Position(Location location, out int line, out int column)
        {
            line = 0;
            column = 0;
            if (location == null || !location.IsInSource) return;
            FileLinePositionSpan mapped = location.GetMappedLineSpan();
            if (!mapped.HasMappedPath || !string.Equals(mapped.Path, UserDocument, StringComparison.OrdinalIgnoreCase)) return;
            line = mapped.StartLinePosition.Line + 1;
            column = mapped.StartLinePosition.Character + 1;
        }
    }
}
