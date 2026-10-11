using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Undo
{
    /// <summary>
    /// undo (SPEC §6.9).
    /// <list type="bullet">
    /// <item>mode auto (default): the target doc must be UI-active (the dispatcher refuses otherwise with NEEDS_ACTIVE_DOC).
    /// The newest non-ui steps of Revit's undo (or redo) stack, <c>steps</c> of them, must all be writes of this broker
    /// session; "MCP ui" steps on top are undone transparently. Each step is one PostCommand(Undo|Redo) from an API
    /// context, confirmed by DocumentChanged(TransactionUndone|Redone) with the expected name within 10 s; the request
    /// stays open meanwhile (ctx.DeferCompletion) and the next step is posted from the next Idling pass.</item>
    /// <item>mode compensate: a new write "MCP &lt;wtag&gt; undo.compensate" that applies the inverse of this session's
    /// newest write(s) from the UndoLedger (delete what they created); irreversible writes give IRREVERSIBLE.</item>
    /// </list>
    /// </summary>
    internal static class UndoOps
    {
        public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

        [Op("undo")]
        public static OpResult Undo(RequestContext ctx)
        {
            UndoLedger.Attach(ctx.Registry);
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "undo needs an open document.");
            string mode = ctx.Args.Enum("mode", "auto", "auto", "compensate");
            int steps = ctx.Args.IntClamped("steps", 1, 1, 10, (field, text) => ctx.Warn(WarningCodes.ValueClamped, field + " " + text));
            bool redo = ctx.Args.Bool("redo", false);

            if (mode == "compensate")
            {
                if (redo)
                {
                    throw OpException.InvalidArgs("redo", "compensate has no redo; call undo {} to undo the compensation write itself",
                        new Dictionary<string, object> { ["mode"] = "compensate", ["steps"] = steps });
                }
                return Compensate(ctx, doc, steps);
            }

            if (ctx.Settings.ExperimentalUndoStack)
            {
                ctx.Warn(WarningCodes.ParamIgnored, "settings experimentalUndoStack: the internal undo API is not verified on this Revit (V12), so Revit's Undo command is used");
            }
            if (ctx.IsInProcess || ctx.Job != null || ctx.WorkItem == null)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "undo posts Revit's Undo command and can only run as a normal queued request.");
            }

            UndoStack stack = UndoStack.From(ctx.Registry.GetJournal(doc), DocumentRegistry.JournalCapacity);
            List<UndoStep> plan = Plan(ctx, doc, stack, steps, redo, out int own);
            if (own < steps)
            {
                ctx.Warn(WarningCodes.PartialResult, (redo ? "redo" : "undo") + " found only " + own.ToString(CultureInfo.InvariantCulture) + " of " +
                    steps.ToString(CultureInfo.InvariantCulture) + " steps of yours" + (stack.Complete ? string.Empty : " in the recent history it keeps"));
            }

            var run = new UndoRun(ctx, doc, plan, redo);
            TimeSpan timeout = TimeSpan.FromMilliseconds(StepTimeout.TotalMilliseconds * plan.Count + 2000);
            ctx.DeferCompletion(run.Completion, timeout, run.OnTimeout);
            run.Start();
            return OpResult.Success(null, "undo posted");
        }

        /// <summary>The steps to post, top first: this session's writes (counted) plus "MCP ui" steps in between.</summary>
        private static List<UndoStep> Plan(RequestContext ctx, Document doc, UndoStack stack, int steps, bool redo, out int own)
        {
            List<UndoStep> source = redo ? stack.Redo : stack.Undo;
            var plan = new List<UndoStep>();
            own = 0;
            for (int i = source.Count - 1; i >= 0 && own < steps; i--)
            {
                UndoStep step = source[i];
                if (step.IsUi)
                {
                    plan.Add(step);
                    continue;
                }
                if (step.IsWriteOf(ctx.ClientKey))
                {
                    plan.Add(step);
                    own++;
                    continue;
                }
                // Every counted step must be this session's (SPEC §6.9): a foreign step on the way blocks the whole call.
                throw Blocked(ctx, doc, step, redo);
            }
            if (own == 0)
            {
                string what = redo ? "redo" : "undo";
                throw new OpException(ErrorCodes.UndoBlocked,
                    "Revit's " + what + " stack of '" + Title(doc) + "' has no change of yours" + (redo ? " to redo" : string.Empty) + (stack.Complete ? "." : " in the recent history the add-in keeps."),
                    new Dictionary<string, object> { ["top"] = null, ["reason"] = "nothing_of_yours", ["compensable"] = !redo && NewestCompensable(ctx, stack) != null });
            }
            // Drop ui steps below the last own write (they are not on top of anything of ours).
            while (plan.Count > 0 && plan[plan.Count - 1].IsUi) plan.RemoveAt(plan.Count - 1);
            return plan;
        }

        private static OpException Blocked(RequestContext ctx, Document doc, UndoStep top, bool redo)
        {
            bool otherSession = top.Class == NameClass.Write && top.Ours != null;
            string who = otherSession ? "another AI session's write" : "a change made in Revit";
            string message = redo
                ? "The newest undone change of '" + Title(doc) + "' is " + who + " (\"" + top.Label + "\"), so there is nothing of yours to redo first."
                : "The newest change of '" + Title(doc) + "' is " + who + " (\"" + top.Label + "\"); Revit can only undo from the top, so your write is not undone.";
            UndoStack stack = UndoStack.From(ctx.Registry.GetJournal(doc), DocumentRegistry.JournalCapacity);
            LedgerRecord compensable = redo ? null : NewestCompensable(ctx, stack);
            var details = new Dictionary<string, object>
            {
                ["top"] = top.Label,
                ["owner"] = otherSession ? "another session" : "user",
                ["compensable"] = compensable != null
            };
            if (compensable != null) details["write"] = compensable.Label;
            return new OpException(ErrorCodes.UndoBlocked, message, details);
        }

        private static LedgerRecord NewestCompensable(RequestContext ctx, UndoStack stack)
        {
            LedgerRecord newest = Targets(ctx, stack, 1).FirstOrDefault();
            return newest != null && UndoLedger.IrreversibleReason(newest) == null ? newest : null;
        }

        /// <summary>This session's newest applied, not yet compensated writes of the target doc (compensation writes skipped).</summary>
        private static List<LedgerRecord> Targets(RequestContext ctx, UndoStack stack, int count)
        {
            HashSet<string> undone = stack.UndoneRequests();
            return UndoLedger.For(ctx.Rid)
                .Where(r => string.Equals(r.ClientKey, ctx.ClientKey, StringComparison.Ordinal) && r.Key != "undo" &&
                            !UndoLedger.IsCompensated(r, undone) && !undone.Contains(r.RequestId))
                .Take(count)
                .ToList();
        }

        // ---------------------------------------------------------------------------------------------------------
        // compensate
        // ---------------------------------------------------------------------------------------------------------

        private static OpResult Compensate(RequestContext ctx, Document doc, int steps)
        {
            UndoStack stack = UndoStack.From(ctx.Registry.GetJournal(doc), DocumentRegistry.JournalCapacity);
            List<LedgerRecord> targets = Targets(ctx, stack, steps);
            if (targets.Count == 0)
            {
                throw new OpException(ErrorCodes.UndoBlocked,
                    "No write of yours in '" + Title(doc) + "' is recorded for compensation (only writes made since this Revit session's add-in start are).",
                    new Dictionary<string, object> { ["top"] = null, ["reason"] = "nothing_recorded", ["compensable"] = false });
            }
            foreach (LedgerRecord record in targets)
            {
                string reason = UndoLedger.IrreversibleReason(record);
                if (reason != null)
                {
                    throw new OpException(ErrorCodes.Irreversible, reason + ", so it cannot be compensated; nothing was changed.",
                        new Dictionary<string, object> { ["write"] = record.Label, ["key"] = record.Key, ["ops"] = record.OpKeys.Cast<object>().ToList() });
                }
            }
            if (targets.Count < steps)
            {
                ctx.Warn(WarningCodes.PartialResult, "only " + targets.Count.ToString(CultureInfo.InvariantCulture) + " of " + steps.ToString(CultureInfo.InvariantCulture) + " writes of yours are recorded for compensation");
            }

            string labels = string.Join(", ", targets.Select(t => t.Label));
            var existing = new List<ElementId>();
            int gone = 0;
            foreach (long id in targets.SelectMany(t => t.Created).Distinct())
            {
                var elementId = new ElementId(id);
                Element element = doc.GetElement(elementId);
                if (element != null && element.IsValidObject) existing.Add(elementId);
                else gone++;
            }
            if (gone > 0) ctx.Warn(WarningCodes.IdsGone, gone.ToString(CultureInfo.InvariantCulture) + " element(s) created by " + labels + " no longer exist");
            if (existing.Count == 0)
            {
                if (!ctx.IsPreview) UndoLedger.MarkCompensated(targets, ctx.RequestId);
                return OpResult.Success(new Dictionary<string, object> { ["compensated"] = targets.Select(t => (object)t.Label).ToList(), ["deleted"] = 0 },
                    "nothing left to compensate: the elements " + labels + " created are already gone");
            }

            var created = new HashSet<long>(targets.SelectMany(t => t.Created));
            OpMeta meta = CompensationMeta(ctx.Meta);
            var op = new PlannedOp
            {
                Index = 0,
                Key = meta.Key,
                Meta = meta,
                Binding = new OpBinding
                {
                    Key = meta.Key,
                    UsesChangeContext = true,
                    InvokeChange = c =>
                    {
                        ICollection<ElementId> deleted = c.Delete(existing);
                        // Only what the write created may go: elements added later and hosted on them would cascade.
                        List<long> others = c.DeletedRecords.Where(d => d.Counted && !created.Contains(d.Id)).Select(d => d.Id).ToList();
                        if (others.Count > 0)
                        {
                            throw new OpException(ErrorCodes.Irreversible,
                                "Compensating " + labels + " would also delete " + others.Count.ToString(CultureInfo.InvariantCulture) +
                                " element(s) added later that depend on it (e.g. " + string.Join(", ", others.Take(5)) + "); nothing was changed.",
                                new Dictionary<string, object> { ["write"] = labels, ["dependents"] = others.Take(50).Cast<object>().ToList(), ["dependentsTotal"] = others.Count });
                        }
                        c.Summary("compensated " + labels + ": deleted " + deleted.Count.ToString(CultureInfo.InvariantCulture) + " element" + (deleted.Count == 1 ? string.Empty : "s"));
                        c.Data(new Dictionary<string, object>
                        {
                            ["compensated"] = targets.Select(t => (object)t.Label).ToList(),
                            ["deleted"] = deleted.Count
                        });
                    }
                },
                RawArgs = ctx.Request.Args ?? new Dictionary<string, object>()
            };
            OpResult result = ChangeEngine.Execute(ctx, new List<PlannedOp> { op }, Naming.Compensation(ctx.WriteTag), isChangeSet: false, setName: null);
            if (result.Ok && result.NeedsConfirm == null && !ctx.IsPreview) UndoLedger.MarkCompensated(targets, ctx.RequestId);
            return result;
        }

        /// <summary>
        /// undo's registry meta adapted for the engine: one "in" transaction. No blast rule: undo has no confirm param, and
        /// compensation deletes only what the session's own write created (anything else makes it IRREVERSIBLE).
        /// </summary>
        private static OpMeta CompensationMeta(OpMeta undo)
        {
            return new OpMeta
            {
                Key = "undo",
                Tool = "undo",
                Op = null,
                Kind = RequestKinds.Write,
                Impl = "addin",
                Scope = undo?.Scope ?? "project_or_family",
                Ui = false,
                Min = undo?.Min ?? 2024,
                Idle = false,
                Tx = "in",
                Blast = new List<string>(),
                Strict = false,
                Job = "never",
                Inproc = false,
                Cs = false
            };
        }

        internal static string Title(Document doc)
        {
            try { return doc?.Title ?? "?"; } catch { return "?"; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // The PostCommand state machine
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Posts Undo/Redo once per planned step and confirms each by its DocumentChanged event. Registry events arrive on
        /// the UI thread; the next step is posted from Idling (an API context once the previous command finished).
        /// </summary>
        private sealed class UndoRun
        {
            private readonly RequestContext _ctx;
            private readonly Document _doc;
            private readonly long _rid;
            private readonly List<UndoStep> _plan;
            private readonly bool _redo;
            private readonly UIApplication _app;
            private readonly TaskCompletionSource<OpResult> _tcs = new TaskCompletionSource<OpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly HashSet<long> _added = new HashSet<long>();
            private readonly HashSet<long> _modified = new HashSet<long>();
            private readonly HashSet<long> _deleted = new HashSet<long>();
            private readonly List<string> _done = new List<string>();
            private int _index;
            private bool _postPending;
            private bool _completed;
            private bool _idlingHooked;
            private bool _registryHooked;
            private DateTime _stepStartedUtc;

            public UndoRun(RequestContext ctx, Document doc, List<UndoStep> plan, bool redo)
            {
                _ctx = ctx;
                _doc = doc;
                _rid = ctx.Rid;
                _plan = plan;
                _redo = redo;
                _app = ctx.App;
            }

            public Task<OpResult> Completion => _tcs.Task;

            private string Expected => _redo ? "redone" : "undone";

            /// <summary>Hooks the events and posts the first step (API context: the request's ExternalEvent pass).</summary>
            public void Start()
            {
                _ctx.Registry.DocumentChangeRecorded += OnRecorded;
                _registryHooked = true;
                try
                {
                    _app.Idling += OnIdling;
                    _idlingHooked = true;
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Warn("undo", "Hooking Idling failed; multi-step undo needs it. " + ex.Message);
                }
                Post(_app);
            }

            private void Post(UIApplication app)
            {
                if (_completed) return;
                RevitCommandId command = RevitCommandId.LookupPostableCommandId(_redo ? PostableCommand.Redo : PostableCommand.Undo);
                if (command == null || !app.CanPostCommand(command))
                {
                    Complete(OpResult.Fail(ErrorCodes.RevitRefused,
                        "Revit does not accept the " + (_redo ? "Redo" : "Undo") + " command right now (another command or edit mode is active?); " + Progress(),
                        new Dictionary<string, object> { ["apiMessage"] = "CanPostCommand false", ["done"] = _done.Cast<object>().ToList() }));
                    return;
                }
                _stepStartedUtc = DateTime.UtcNow;
                app.PostCommand(command);
            }

            private void OnRecorded(DocumentChangeRecord change)
            {
                try
                {
                    if (_completed || change == null || change.Rid != _rid || change.Entry == null) return;
                    string operation = change.Entry.Operation;
                    if (operation != "undone" && operation != "redone") return;
                    if (operation != Expected)
                    {
                        Complete(Unconfirmed("Revit reported '" + operation + "' instead of '" + Expected + "'"));
                        return;
                    }
                    UndoStep step = _plan[_index];
                    UndoStep previous = _index > 0 ? _plan[_index - 1] : null;
                    if (!step.SameNames(change.Entry.Names))
                    {
                        // A group may report one event per inner transaction: more events of the step just confirmed.
                        if (previous != null && previous.SameNames(change.Entry.Names)) { Collect(change); return; }
                        string got = change.Entry.Names?.FirstOrDefault() ?? "?";
                        Complete(Unconfirmed("Revit " + (_redo ? "redid" : "undid") + " \"" + got + "\" instead of \"" + step.Label + "\""));
                        return;
                    }
                    Collect(change);
                    _done.Add(step.Label);
                    _index++;
                    if (_index >= _plan.Count) Complete(Success());
                    else _postPending = true;
                }
                catch (Exception ex)
                {
                    Complete(Dispatcher.MapException(_ctx, ex));
                }
            }

            private void OnIdling(object sender, IdlingEventArgs args)
            {
                var app = sender as UIApplication;
                try
                {
                    if (_completed)
                    {
                        Unhook(app);
                        return;
                    }
                    if (DateTime.UtcNow - _stepStartedUtc > StepTimeout)
                    {
                        Complete(Unconfirmed("no confirmation within " + StepTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s"));
                        Unhook(app);
                        return;
                    }
                    if (_postPending && app != null)
                    {
                        _postPending = false;
                        Post(app);
                    }
                }
                catch (Exception ex)
                {
                    Complete(Dispatcher.MapException(_ctx, ex));
                    Unhook(app);
                }
            }

            /// <summary>The deferral timeout (UI thread, API context).</summary>
            public OpResult OnTimeout()
            {
                OpResult result = Unconfirmed("no confirmation within " + StepTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s per step");
                Complete(result);
                Unhook(_app);
                return result;
            }

            private void Collect(DocumentChangeRecord change)
            {
                foreach (ElementId id in change.AddedIds ?? new List<ElementId>()) if (id != null) _added.Add(id.Value);
                foreach (ElementId id in change.ModifiedIds ?? new List<ElementId>()) if (id != null) _modified.Add(id.Value);
                foreach (ElementId id in change.DeletedIds ?? new List<ElementId>()) if (id != null) _deleted.Add(id.Value);
            }

            private OpResult Success()
            {
                List<string> own = _plan.Where(s => !s.IsUi).Select(s => s.Label).ToList();
                int uiSteps = _plan.Count(s => s.IsUi);
                string verb = _redo ? "redid" : "undid";
                string summary = verb + " " + (own.Count == 1 ? own[0] : own.Count.ToString(CultureInfo.InvariantCulture) + " writes (" + string.Join(", ", own) + ")") +
                                 (uiSteps > 0 ? " (and " + uiSteps.ToString(CultureInfo.InvariantCulture) + " temporary view change" + (uiSteps == 1 ? string.Empty : "s") + " on top)" : string.Empty);
                var data = new Dictionary<string, object>
                {
                    [_redo ? "redone" : "undone"] = own.Cast<object>().ToList(),
                    ["steps"] = own.Count
                };
                if (uiSteps > 0) data["ui_steps"] = uiSteps;
                List<long> added = _added.Where(id => !_deleted.Contains(id)).ToList();
                List<long> deleted = _deleted.Where(id => !_added.Contains(id)).ToList();
                List<long> modified = _modified.Where(id => !_added.Contains(id) && !_deleted.Contains(id)).ToList();
                return OpResult.Success(data, summary).WithChanges(added, modified, deleted);
            }

            private OpResult Unconfirmed(string why)
            {
                return OpResult.Fail(ErrorCodes.UndoUnconfirmed,
                    (_redo ? "Redo" : "Undo") + " was not confirmed: " + why + "; " + Progress(),
                    new Dictionary<string, object> { ["done"] = _done.Cast<object>().ToList(), ["expected"] = _index < _plan.Count ? _plan[_index].Label : null });
            }

            private string Progress()
            {
                return _done.Count == 0
                    ? "nothing was confirmed as " + Expected
                    : Expected + " so far: " + string.Join(", ", _done);
            }

            private void Complete(OpResult result)
            {
                if (_completed) return;
                _completed = true;
                if (_registryHooked)
                {
                    try { _ctx.Registry.DocumentChangeRecorded -= OnRecorded; } catch { }
                    _registryHooked = false;
                }
                _tcs.TrySetResult(result);
            }

            private void Unhook(UIApplication app)
            {
                if (!_idlingHooked) return;
                try
                {
                    (app ?? _app).Idling -= OnIdling;
                    _idlingHooked = false;
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Debug("undo", "Unhooking Idling failed: " + ex.Message);
                }
            }
        }
    }
}
