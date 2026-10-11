using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Code
{
    /// <summary>
    /// run_csharp (SPEC §11): opt-in C# on the Revit API.
    /// <list type="bullet">
    /// <item>Gate: settings.json enableCodeExecution, re-read on every call (the broker's listing is not trusted).</item>
    /// <item>Consent: the per-Revit-session TaskDialog when codeExecution.consentPrompt is on, shown as job step 1.</item>
    /// <item>Modes: read (TempScope, no transaction, own transactions denied), dry_run (TempScope + "MCP temp run_csharp"
    /// transaction unless the code opens its own; changes reported, NOT APPLIED with rule code_commit), commit (only with
    /// the broker's confirmed plan; WriteScope "MCP &lt;wtag&gt; run_csharp" + Assimilate).</item>
    /// <item>Deny lists by resolved symbol (always; lifecycle unless commit + allowUnsafeApis; transactions in read).</item>
    /// <item>Cooperative timeout/cancel through the generated guard; output projection; audit line per call.</item>
    /// </list>
    /// </summary>
    internal static class RunCsharpOps
    {
        public const int DefaultTimeoutSec = 30;
        public const int MaxArgTimeoutSec = 45;
        private const int SampleIds = 20;

        [Op("run_csharp")]
        public static OpResult RunCsharp(RequestContext ctx)
        {
            McpSettings settings = ctx.SettingsStore?.GetFresh() ?? ctx.Settings;
            CodeConsent.Publish(settings);
            string code = ctx.Args.Str("code");
            var audit = NewAudit(ctx, code);
            try
            {
                if (!settings.EnableCodeExecution)
                {
                    throw new OpException(ErrorCodes.CodeExecutionDisabled,
                        "C# code execution is disabled in this Revit (settings.json enableCodeExecution is false).",
                        new Dictionary<string, object> { ["settings"] = McpRuntime.Home?.SettingsFile });
                }
                string mode = ctx.Args.Enum("mode", "dry_run", "read", "dry_run", "commit");
                audit.Mode = mode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    throw OpException.InvalidArgs("code", "is required: a C# method body using doc, uidoc, app, args and log, ending with return <value>;",
                        new Dictionary<string, object> { ["code"] = "return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();", ["mode"] = "read" });
                }
                if (mode == "commit" && (ctx.IsPreview || ctx.Confirmed == null))
                {
                    throw new OpException(ErrorCodes.InvalidArgs,
                        "mode commit needs the confirm token of a dry run of this exact code (run it with mode dry_run first and ask the user).",
                        new Dictionary<string, object> { ["param"] = "confirm", ["reason"] = "commit without a confirmed dry run", ["example"] = new Dictionary<string, object> { ["code"] = Cut(code, 300), ["mode"] = "dry_run" } });
                }

                string consent = CodeConsent.State(settings);
                audit.Consent = consent;
                if (consent == "blocked")
                {
                    throw new OpException(ErrorCodes.CodeExecutionDeclined,
                        "Code execution was blocked in Revit's prompt; it is not asked again for " + CodeConsent.BlockedForSeconds.ToString(CultureInfo.InvariantCulture) + " s. Nothing ran.",
                        new Dictionary<string, object> { ["retryAfterS"] = CodeConsent.BlockedForSeconds });
                }
                if (consent == "pending")
                {
                    if (ctx.IsInProcess)
                    {
                        throw new OpException(ErrorCodes.CodeExecutionDeclined, "Code execution needs the user's consent in Revit first.");
                    }
                    var steps = new List<Func<JobStep, StepResult>>
                    {
                        step => AskConsent(step, ctx, settings, code, mode, audit),
                        step => StepResult.Finish(Execute(ctx, McpRuntime.Settings?.GetFresh() ?? settings, code, mode, audit))
                    };
                    return ctx.Jobs.Start(ctx, steps, "waiting for the user's answer to the code-execution prompt in Revit", 2);
                }
                return Execute(ctx, settings, code, mode, audit);
            }
            catch (Exception ex)
            {
                OpResult failure = Dispatcher.MapException(ctx, ex);
                Finish(audit, failure, code);
                return failure;
            }
        }

        private static StepResult AskConsent(JobStep step, RequestContext ctx, McpSettings settings, string code, string mode, CodeAuditEntry audit)
        {
            if (step.CancelRequested) return StepResult.Fail(ErrorCodes.RequestCancelled, "The code-execution request was cancelled.");
            string client = (ctx.ClientKey ?? string.Empty).Split('#')[0];
            bool allowed;
            try
            {
                allowed = CodeConsent.Ask(client, audit.CodeSha256, code, SafeTitle(ctx.Doc), mode);
            }
            finally
            {
                CodeConsent.Publish(settings);
            }
            audit.Consent = allowed ? "granted" : "declined";
            if (!allowed)
            {
                OpResult declined = OpResult.Fail(ErrorCodes.CodeExecutionDeclined, "The user did not allow code execution in Revit's prompt; nothing ran.");
                Finish(audit, declined, code);
                return StepResult.Finish(declined);
            }
            step.Progress("running the code", 1, 2);
            return StepResult.Next("running the code", 1, 2);
        }

        /// <summary>Analyze, compile and run in the scope of <paramref name="mode"/>; writes the audit line. Never throws.</summary>
        private static OpResult Execute(RequestContext ctx, McpSettings settings, string code, string mode, CodeAuditEntry audit)
        {
            OpResult result;
            try
            {
                result = ExecuteCore(ctx, settings, code, mode, audit);
            }
            catch (Exception ex)
            {
                result = Dispatcher.MapException(ctx, ex);
            }
            Finish(audit, result, code);
            return result;
        }

        private static OpResult ExecuteCore(RequestContext ctx, McpSettings settings, string code, string mode, CodeAuditEntry audit)
        {
            if (!settings.EnableCodeExecution)
            {
                throw new OpException(ErrorCodes.CodeExecutionDisabled, "C# code execution was disabled in settings.json; nothing ran.");
            }
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "run_csharp needs an open document.");

            // 1. Source, analysis (deny lists) and compilation.
            string source = ScriptSource.Build(code, out bool expressionMode);
            string sourceSha = ScriptSource.Sha256(source);
            ScriptAnalysisResult analysis = ScriptHost.Analyze(source, sourceSha);
            audit.Compiler = ScriptHost.CompilerVersion;
            if (analysis.Diagnostics.Count > 0) throw CompileError(analysis.Diagnostics, expressionMode);

            bool allowUnsafe = mode == "commit" && settings.CodeExecution.AllowUnsafeApis;
            List<ScriptDeniedSymbol> denied = analysis.DeniedSymbols.Where(d =>
                d.DenyList == "always" ||
                (d.DenyList == "lifecycle" && !allowUnsafe) ||
                (d.DenyList == "transaction" && mode == "read")).ToList();
            if (denied.Count > 0) throw DeniedError(denied, mode);

            CompiledScript compiled = ScriptHost.GetOrCompile(source, sourceSha, out ScriptCompileResult failure);
            if (compiled == null) throw CompileError(failure?.Diagnostics ?? new List<ScriptDiagnostic>(), expressionMode);

            // 2. Arguments, log, limits.
            int timeoutSec = TimeoutSeconds(ctx, settings);
            var log = new ScriptLog(settings.CodeExecution.MaxOutputKB, doc);
            IDictionary<string, object> scriptArgs = ToBcl(ctx.Args.Dict("args")) as IDictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
            object[] arguments = { doc, ctx.UiDoc, ctx.RevitApp, scriptArgs, new Action<object>(log.Write) };
            long stampBefore = ctx.Generation;

            // 3. Run in the mode's scope, collecting DocumentChanged ids.
            var changes = new ChangeCollector(doc);
            var userFailures = new UserTransactionFailures();
            ScriptRun run = null;
            TxResult tx = null;
            DocumentRegistry registry = ctx.Registry;
            Application app = ctx.RevitApp;
            Func<ScriptRun> invoke = () => ScriptHost.Invoke(compiled, arguments, TimeSpan.FromSeconds(timeoutSec), () => ctx.CancelRequested,
                value => ScriptOutput.Project(value, doc));
            if (registry != null) registry.RawDocumentChanged += changes.OnChanged;
            if (analysis.UsesTransactions && app != null) app.FailuresProcessing += userFailures.OnFailuresProcessing;
            try
            {
                switch (mode)
                {
                    case "read":
                        using (new TempScope(doc, Naming.Purposes.RunCSharp))
                        {
                            run = invoke();
                            changes.Stop();
                        }
                        break;
                    case "dry_run":
                        using (var temp = new TempScope(doc, Naming.Purposes.RunCSharp))
                        {
                            if (analysis.UsesTransactions) run = invoke();
                            else tx = RunInTransaction(action => temp.Run(action), invoke, out run);
                            changes.Stop();
                        }
                        break;
                    default:
                        using (var scope = new WriteScope(doc, Naming.Write(ctx.WriteTag, "run_csharp")))
                        {
                            if (analysis.UsesTransactions) run = invoke();
                            else tx = RunInTransaction(action => scope.RunTransaction(action), invoke, out run);
                            changes.Stop();
                            if (run.Error == null && (tx == null || tx.Committed) && !userFailures.RolledBack)
                            {
                                TransactionStatus status = scope.Assimilate();
                                if (status != TransactionStatus.Committed)
                                {
                                    throw new OpException(ErrorCodes.RevitTransactionRolledBack, "Revit did not commit the run_csharp group (" + status + "); nothing changed.",
                                        new Dictionary<string, object> { ["status"] = status.ToString() });
                                }
                            }
                        }
                        break;
                }
            }
            finally
            {
                changes.Stop();
                if (registry != null) registry.RawDocumentChanged -= changes.OnChanged;
                if (analysis.UsesTransactions && app != null) app.FailuresProcessing -= userFailures.OnFailuresProcessing;
            }

            // 4. Outcome.
            if (run.TimedOut)
            {
                if (run.Cancelled) throw new OpException(ErrorCodes.RequestCancelled, "The script was cancelled; everything it changed was rolled back.");
                throw new OpException(ErrorCodes.CodeTimeout,
                    "The script exceeded its time limit of " + timeoutSec.ToString(CultureInfo.InvariantCulture) + " s" + At(run) + " and was stopped; everything was rolled back.",
                    new Dictionary<string, object> { ["timeoutSec"] = timeoutSec, ["line"] = run.Line, ["col"] = run.Column });
            }
            if (run.Error != null) throw RuntimeError(run, mode);
            if (tx != null && !tx.Committed) throw tx.ToException("run_csharp");
            if (userFailures.RolledBack)
            {
                throw new OpException(ErrorCodes.RevitTransactionRolledBack,
                    "Revit rolled back a transaction of the script: " + userFailures.Errors.FirstOrDefault()?.Text + "; nothing was committed.",
                    new Dictionary<string, object> { ["failures"] = userFailures.Errors.Select(f => (object)f.ToWire()).ToList() });
            }

            changes.Net(out List<long> added, out List<long> modified, out List<long> deleted);
            audit.Added = added.Count;
            audit.Modified = modified.Count;
            audit.Deleted = deleted.Count;

            var data = new Dictionary<string, object>(StringComparer.Ordinal) { ["result"] = run.Value };
            string logText = log.Text;
            if (!string.IsNullOrEmpty(logText)) data["log"] = logText;
            if (mode != "read")
            {
                data["changes"] = new Dictionary<string, object>
                {
                    ["added"] = added.Count,
                    ["modified"] = modified.Count,
                    ["deleted"] = deleted.Count,
                    ["sample"] = new Dictionary<string, object>
                    {
                        ["added"] = added.Take(SampleIds).Cast<object>().ToList(),
                        ["modified"] = modified.Take(SampleIds).Cast<object>().ToList(),
                        ["deleted"] = deleted.Take(SampleIds).Cast<object>().ToList()
                    }
                };
            }
            data["ms"] = new Dictionary<string, object> { ["compile"] = compiled.CompileMs, ["run"] = run.DurationMs };
            if (expressionMode) data["expression"] = true;

            string counts = "added " + added.Count.ToString(CultureInfo.InvariantCulture) + ", modified " + modified.Count.ToString(CultureInfo.InvariantCulture) +
                            ", deleted " + deleted.Count.ToString(CultureInfo.InvariantCulture);
            string returned = ReturnedText(run.Value);
            OpResult result;
            switch (mode)
            {
                case "read":
                    result = OpResult.Success(data, "read-only run finished in " + run.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms; " + returned + "; nothing was changed");
                    break;
                case "dry_run":
                {
                    bool changed = added.Count + modified.Count + deleted.Count > 0;
                    string summary = changed
                        ? "dry run would have " + counts + " elements; " + returned + "; everything was rolled back"
                        : "dry run changed nothing; " + returned + "; nothing to commit";
                    result = OpResult.Success(data, summary);
                    if (changed)
                    {
                        result.WithNeedsConfirm(new NeedsConfirm
                        {
                            Rule = "code_commit",
                            Plan = "commit the C# code (sha " + Short(audit.CodeSha256) + "): " + counts + " elements",
                            Stamp = stampBefore,
                            Blast = new BlastInfo { CreateTotal = added.Count, ModifyTotal = modified.Count, DeleteTotal = deleted.Count, Sample = deleted.Take(50).ToList() }
                        });
                    }
                    break;
                }
                default:
                    result = OpResult.Success(data, "committed the C# code: " + counts + " elements; " + returned)
                        .WithChanges(added, modified, deleted);
                    break;
            }

            if (tx != null) foreach (CapturedFailure warning in tx.Warnings) result.Warn(WarningCodes.RevitWarning, warning.Text, warning.FailingIds);
            foreach (CapturedFailure warning in userFailures.Warnings) result.Warn(WarningCodes.RevitWarning, warning.Text, warning.FailingIds);
            if (log.Truncated) result.Warn(WarningCodes.PartialResult, "log output was cut at " + settings.CodeExecution.MaxOutputKB.ToString(CultureInfo.InvariantCulture) + " KB (codeExecution.maxOutputKB)");
            audit.Warnings = result.Warnings.Count;
            return result;
        }

        /// <summary>Runs the script inside one host transaction; a script failure rolls that transaction back.</summary>
        private static TxResult RunInTransaction(Func<Action, TxResult> runTransaction, Func<ScriptRun> invoke, out ScriptRun run)
        {
            ScriptRun captured = null;
            TxResult tx = null;
            try
            {
                tx = runTransaction(() =>
                {
                    captured = invoke();
                    if (captured.Error != null) throw new ScriptFailedException();
                });
            }
            catch (ScriptFailedException)
            {
                // The transaction was rolled back; captured holds the error.
            }
            run = captured ?? new ScriptRun { Error = new InvalidOperationException("The script did not run.") };
            return tx;
        }

        private sealed class ScriptFailedException : Exception
        {
        }

        // ---------------------------------------------------------------------------------------------------------
        // Errors
        // ---------------------------------------------------------------------------------------------------------

        private static OpException CompileError(IList<ScriptDiagnostic> diagnostics, bool expressionMode)
        {
            ScriptDiagnostic first = diagnostics.FirstOrDefault(d => d.Severity == "error") ?? diagnostics.FirstOrDefault() ??
                                     new ScriptDiagnostic { Id = "CS0000", Message = "The code did not compile." };
            string where = first.Line > 0 ? "line " + first.Line.ToString(CultureInfo.InvariantCulture) + ":" + first.Column.ToString(CultureInfo.InvariantCulture) + " " : string.Empty;
            string hint = expressionMode ? " (the code was read as one expression; end statements with ';' and return a value)" : string.Empty;
            return new OpException(ErrorCodes.CodeCompileError, "The code does not compile: " + where + first.Id + " " + first.Message + hint,
                new Dictionary<string, object>
                {
                    ["line"] = first.Line,
                    ["col"] = first.Column,
                    ["message"] = first.Message,
                    ["id"] = first.Id,
                    ["errors"] = diagnostics.Take(5).Select(d => (object)(d.Line.ToString(CultureInfo.InvariantCulture) + ":" + d.Column.ToString(CultureInfo.InvariantCulture) + " " + d.Id + " " + d.Message)).ToList()
                });
        }

        private static OpException DeniedError(List<ScriptDeniedSymbol> denied, string mode)
        {
            ScriptDeniedSymbol first = denied[0];
            string why;
            switch (first.DenyList)
            {
                case "transaction":
                    why = "read mode cannot open transactions (use mode dry_run)";
                    break;
                case "lifecycle":
                    why = mode == "commit"
                        ? "it changes files or documents outside the undo stack (allowed in commit only with codeExecution.allowUnsafeApis)"
                        : "it changes files or documents outside the undo stack, which a " + mode.Replace('_', ' ') + " cannot roll back";
                    break;
                default:
                    why = "it is never allowed in scripts";
                    break;
            }
            string where = first.Line > 0 ? " (line " + first.Line.ToString(CultureInfo.InvariantCulture) + ":" + first.Column.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty;
            return new OpException(ErrorCodes.CodeDeniedApi, first.Symbol + where + " is not allowed: " + why + "; nothing ran.",
                new Dictionary<string, object>
                {
                    ["symbol"] = first.Symbol,
                    ["line"] = first.Line,
                    ["col"] = first.Column,
                    ["list"] = first.DenyList,
                    ["mode"] = mode,
                    ["denied"] = denied.Take(10).Select(d => (object)(d.Symbol + " [" + d.DenyList + "]")).ToList()
                });
        }

        private static OpException RuntimeError(ScriptRun run, string mode)
        {
            Exception error = run.Error;
            string type = error.GetType().Name;
            string hint = string.Empty;
            if (mode == "read" && type.IndexOf("ModificationOutsideTransaction", StringComparison.Ordinal) >= 0)
            {
                hint = " (read mode has no transaction; use mode dry_run to try changes)";
            }
            else if (error is InsufficientExecutionStackException)
            {
                hint = " (calls nested deeper than " + ScriptSource.MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels)";
            }
            return new OpException(ErrorCodes.CodeRuntimeError,
                "The script threw " + type + At(run) + ": " + error.Message + hint + "; everything was rolled back.",
                new Dictionary<string, object> { ["line"] = run.Line, ["col"] = run.Column, ["message"] = error.Message, ["exception"] = error.GetType().FullName });
        }

        private static string At(ScriptRun run)
        {
            return run.Line > 0 ? " at line " + run.Line.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>timeout arg (1-45 s, default 30) capped by codeExecution.timeoutSec; isolated e2e homes default to the setting.</summary>
        private static int TimeoutSeconds(RequestContext ctx, McpSettings settings)
        {
            int cap = Math.Max(1, settings.CodeExecution.TimeoutSec);
            bool isolated = McpRuntime.TestOpsEnabled;
            int max = isolated ? cap : Math.Min(cap, MaxArgTimeoutSec);
            int? requested = ctx.Args.IntOpt("timeout");
            if (requested.HasValue) return Math.Max(1, Math.Min(requested.Value, max));
            return isolated ? cap : Math.Min(DefaultTimeoutSec, max);
        }

        private static CodeAuditEntry NewAudit(RequestContext ctx, string code)
        {
            var audit = new CodeAuditEntry
            {
                Client = (ctx.ClientKey ?? string.Empty).Split('#')[0],
                Session = ctx.ClientKey,
                RequestId = ctx.RequestId,
                WriteTag = ctx.Request.WriteTag,
                RevitYear = ctx.Year,
                DocTitle = SafeTitle(ctx.Doc),
                Mode = ctx.Args.Str("mode") ?? "dry_run",
                CodeSha256 = ScriptSource.Sha256(code ?? string.Empty),
                ArgsSha256 = CodeAudit.ArgsSha(ctx.Args.Dict("args")),
                Token = ctx.Args.Str("audit_token")
            };
            try { audit.DocKey = ctx.Doc == null ? null : ctx.Registry?.GetKey(ctx.Doc); } catch { }
            return audit;
        }

        private static void Finish(CodeAuditEntry audit, OpResult result, string code)
        {
            if (audit == null || audit.Outcome == "written") return;
            audit.DurationMs = audit.Watch.ElapsedMilliseconds;
            if (result == null || !result.Ok)
            {
                audit.Outcome = "error";
                audit.Error = result?.Code;
                audit.ErrorMessage = Cut(result?.Message, 500);
            }
            else
            {
                audit.Outcome = result.NeedsConfirm != null ? "not_applied" : "ok";
            }
            if (result?.HandoffJobId != null) return;   // the job's steps write the line
            CodeAudit.Write(McpRuntime.Home, audit, code);
            audit.Outcome = "written";
        }

        /// <summary>JSON values → BCL types only (Dictionary, List, string, double, long, bool) per SPEC §11.3.</summary>
        private static object ToBcl(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case bool b: return b;
                case int i: return (long)i;
                case long l: return l;
                case short sh: return (long)sh;
                case decimal d: return d == Math.Truncate(d) && Math.Abs(d) < long.MaxValue ? (object)(long)d : (double)d;
                case double db: return db;
                case float f: return (double)f;
            }
            IDictionary<string, object> map = Wire.AsMap(value);
            if (map != null)
            {
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in map) copy[pair.Key] = ToBcl(pair.Value);
                return copy;
            }
            IList<object> list = Wire.AsList(value);
            if (list != null) return list.Select(ToBcl).ToList();
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string ReturnedText(object value)
        {
            switch (value)
            {
                case null: return "returned nothing";
                case string s: return "returned \"" + Cut(s, 60) + "\"";
                case bool b: return "returned " + (b ? "true" : "false");
                case long _:
                case double _:
                    return "returned " + Convert.ToString(value, CultureInfo.InvariantCulture);
                case IList<object> list: return "returned " + list.Count.ToString(CultureInfo.InvariantCulture) + " items";
                case IDictionary<string, object> map: return "returned an object with " + map.Count.ToString(CultureInfo.InvariantCulture) + " keys";
                default: return "returned a value";
            }
        }

        private static string Short(string sha)
        {
            return string.IsNullOrEmpty(sha) ? "?" : sha.Substring(0, Math.Min(12, sha.Length));
        }

        private static string Cut(string text, int max)
        {
            if (text == null) return null;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc?.Title; } catch { return null; }
        }

        /// <summary>Element ids of the target document's DocumentChanged events while the script runs (temp included).</summary>
        private sealed class ChangeCollector
        {
            private readonly Document _doc;
            private readonly HashSet<long> _added = new HashSet<long>();
            private readonly HashSet<long> _modified = new HashSet<long>();
            private readonly HashSet<long> _deleted = new HashSet<long>();
            private bool _stopped;

            public ChangeCollector(Document doc)
            {
                _doc = doc;
            }

            public void Stop()
            {
                _stopped = true;
            }

            public void OnChanged(DocumentChangedEventArgs args, bool temp)
            {
                if (_stopped || args == null) return;
                try
                {
                    if (args.Operation != UndoOperation.TransactionCommitted) return;
                    Document document = args.GetDocument();
                    if (document == null || !document.Equals(_doc)) return;
                    foreach (ElementId id in args.GetAddedElementIds()) _added.Add(id.Value);
                    foreach (ElementId id in args.GetModifiedElementIds()) _modified.Add(id.Value);
                    foreach (ElementId id in args.GetDeletedElementIds()) _deleted.Add(id.Value);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Debug("code", "Collecting script changes failed: " + ex.Message);
                }
            }

            public void Net(out List<long> added, out List<long> modified, out List<long> deleted)
            {
                added = _added.Where(id => !_deleted.Contains(id)).OrderBy(id => id).ToList();
                modified = _modified.Where(id => !_added.Contains(id) && !_deleted.Contains(id)).OrderBy(id => id).ToList();
                deleted = _deleted.Where(id => !_added.Contains(id)).OrderBy(id => id).ToList();
            }
        }

        /// <summary>
        /// Failure handling for transactions the script opens itself (their names are not "MCP ...", so the add-in's
        /// safety net ignores them): warnings are deleted and reported, errors roll the transaction back instead of
        /// showing a modal Revit dialog.
        /// </summary>
        private sealed class UserTransactionFailures
        {
            public List<CapturedFailure> Warnings { get; } = new List<CapturedFailure>();
            public List<CapturedFailure> Errors { get; } = new List<CapturedFailure>();
            public bool RolledBack { get; private set; }

            public void OnFailuresProcessing(object sender, FailuresProcessingEventArgs args)
            {
                try
                {
                    FailuresAccessor accessor = args.GetFailuresAccessor();
                    if (accessor == null) return;
                    string name = accessor.GetTransactionName() ?? string.Empty;
                    if (Naming.IsMcpName(name)) return;
                    bool error = false;
                    foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
                    {
                        CapturedFailure captured = CapturedFailure.From(failure);
                        if (failure.GetSeverity() == FailureSeverity.Warning)
                        {
                            Warnings.Add(captured);
                            accessor.DeleteWarning(failure);
                        }
                        else
                        {
                            Errors.Add(captured);
                            error = true;
                        }
                    }
                    if (error)
                    {
                        RolledBack = true;
                        args.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Debug("code", "Script failure handling failed: " + ex.Message);
                }
            }
        }
    }
}
