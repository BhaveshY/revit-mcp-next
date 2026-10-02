using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>One op of a change set (a single tool call is a change set of one op).</summary>
    internal sealed class PlannedOp
    {
        public int Index { get; set; }
        public string Key { get; set; } = string.Empty;
        public OpMeta Meta { get; set; }
        public OpBinding Binding { get; set; }
        /// <summary>Args as sent (may contain $refs; resolved just before the op runs).</summary>
        public IDictionary<string, object> RawArgs { get; set; } = new Dictionary<string, object>(StringComparer.Ordinal);
    }

    /// <summary>What an op did (input for $refs, blast rules and the response).</summary>
    internal sealed class OpOutcome
    {
        public int Index { get; set; }
        public string Key { get; set; }
        public OpMeta Meta { get; set; }
        public List<ElementId> Created { get; set; } = new List<ElementId>();
        public List<ElementId> Modified { get; set; } = new List<ElementId>();
        public List<DeletedRecord> Deleted { get; set; } = new List<DeletedRecord>();
        public int Affected { get; set; }
        public Dictionary<string, object> Outputs { get; set; } = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<BridgeWarning> Warnings { get; set; } = new List<BridgeWarning>();
        public string Summary { get; set; }
        public object Data { get; set; }
        public ElementId Primary { get; set; }
        public string PendingRule { get; set; }
        public object PendingPlan { get; set; }

        internal static OpOutcome From(PlannedOp op, ChangeContext c)
        {
            return new OpOutcome
            {
                Index = op.Index,
                Key = op.Key,
                Meta = op.Meta,
                Created = c.CreatedIds.ToList(),
                Modified = c.ModifiedIds.ToList(),
                Deleted = c.DeletedRecords.ToList(),
                Affected = c.AffectedCount,
                Outputs = new Dictionary<string, object>(c.Outputs, StringComparer.Ordinal),
                Warnings = c.Warnings.ToList(),
                Summary = c.SummaryText,
                Data = c.DataValue,
                Primary = c.PrimaryId,
                PendingRule = c.PendingRule,
                PendingPlan = c.PendingPlan
            };
        }
    }

    /// <summary>
    /// The write engine (SPEC §6): every write runs in one WriteScope (TransactionGroup, Assimilate on success) with a
    /// Transaction per op (tx "in"), handler-owned scopes (tx "own") or no transaction (tx "none", family segments).
    /// After execution the blast rules (settings confirm.*) are evaluated from the recorded sets; when one triggers
    /// and the request is not a confirmed apply the group is rolled back and needsConfirm is returned. Preview runs
    /// the same pipeline in a TempScope. Confirmed applies check per-element stamps and the delete set (CONFIRM_STALE).
    /// </summary>
    internal static partial class ChangeEngine
    {
        public const int MaxChangeSetOps = 100;
        private const int MaxDeleteSet = 100000;
        private static readonly Regex RefPattern = new Regex(
            @"^\$(?<n>\d+|prev)(?:\.(?:(?<ids>ids)(?:\[(?<k>\d+)\])?|(?<out>[A-Za-z_][A-Za-z0-9_]*)))?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>A single tool call (ChangeContext handler) as a change set of one op.</summary>
        public static OpResult RunSingle(RequestContext ctx, OpBinding binding)
        {
            var op = new PlannedOp
            {
                Index = 0,
                Key = ctx.Key,
                Meta = ctx.Meta,
                Binding = binding,
                RawArgs = ctx.Request.Args ?? new Dictionary<string, object>()
            };
            if (ctx.Meta.Kind == RequestKinds.Ui) return RunUiOp(ctx, op);
            return Execute(ctx, new List<PlannedOp> { op }, Naming.Write(ctx.WriteTag, ctx.Key), isChangeSet: false, setName: null);
        }

        /// <summary>change_set: args {ops:[{tool, op|kind|check|format, ...args}], name?}.</summary>
        [Op("change_set")]
        public static OpResult ChangeSet(RequestContext ctx)
        {
            List<PayloadReader> entries = ctx.Args.SubList("ops");
            if (entries.Count == 0) throw OpException.InvalidArgs("ops", "needs at least one op {tool, op, ...}");
            if (entries.Count > MaxChangeSetOps)
            {
                throw OpException.InvalidArgs("ops", "has " + entries.Count.ToString(CultureInfo.InvariantCulture) + " ops; at most " +
                    MaxChangeSetOps.ToString(CultureInfo.InvariantCulture) + " are allowed per change_set");
            }

            var ops = new List<PlannedOp>(entries.Count);
            for (int index = 0; index < entries.Count; index++)
            {
                ops.Add(PlanChangeSetOp(ctx, entries[index], index));
            }
            string name = ctx.Args.Str("name");
            return Execute(ctx, ops, Naming.ChangeSet(ctx.WriteTag, name, ops.Count), isChangeSet: true, setName: name);
        }

        private static PlannedOp PlanChangeSetOp(RequestContext ctx, PayloadReader entry, int index)
        {
            string label = "ops[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            EmbeddedCatalog catalog = ctx.Ops.Catalog;
            string key = entry.Str("key");
            string tool = entry.Str("tool");
            var discriminatorKeys = new HashSet<string>(StringComparer.Ordinal) { "tool", "key", "args" };
            if (string.IsNullOrWhiteSpace(key))
            {
                if (string.IsNullOrWhiteSpace(tool)) throw OpException.InvalidArgs(label + ".tool", "is required (the tool of this op)");
                string discriminator = catalog.DiscriminatorOf(tool) ?? "op";
                string value = entry.Str(discriminator) ?? entry.Str("op");
                discriminatorKeys.Add(discriminator);
                discriminatorKeys.Add("op");
                key = string.IsNullOrWhiteSpace(value) ? tool : tool + "." + value;
            }

            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            IDictionary<string, object> nested = entry.Dict("args");
            if (nested != null) foreach (KeyValuePair<string, object> pair in nested) args[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, object> pair in entry.Raw)
            {
                if (!discriminatorKeys.Contains(pair.Key) && !args.ContainsKey(pair.Key)) args[pair.Key] = pair.Value;
            }

            OpMeta meta = ctx.Ops.GetMeta(key);
            OpBinding binding = ctx.Ops.GetBinding(key);
            if (meta == null)
            {
                throw new OpException(ErrorCodes.UnknownOp, label + ": unknown op '" + key + "'.",
                    new Dictionary<string, object> { ["op"] = index, ["key"] = key, ["ops"] = ctx.Ops.Closest(key), ["closest"] = ctx.Ops.Closest(key, 1).FirstOrDefault() });
            }
            if (!meta.Cs)
            {
                throw new OpException(ErrorCodes.InvalidArgs, label + ": " + key + " cannot run inside change_set; call it on its own.",
                    new Dictionary<string, object>
                    {
                        ["op"] = index,
                        ["key"] = key,
                        ["param"] = label,
                        ["reason"] = "not allowed in change_set",
                        ["standalone"] = new Dictionary<string, object> { ["tool"] = meta.Tool, ["op"] = meta.Op }
                    });
            }
            if (meta.Min > ctx.Year)
            {
                throw new OpException(ErrorCodes.UnsupportedVersion, label + ": " + key + " needs Revit " + meta.Min.ToString(CultureInfo.InvariantCulture) + " or newer.",
                    new Dictionary<string, object> { ["op"] = index, ["min"] = meta.Min, ["year"] = ctx.Year });
            }
            if (binding == null || !binding.UsesChangeContext)
            {
                throw new OpException(ErrorCodes.AddinOutdated, label + ": " + key + " has no change-set handler in this add-in build.",
                    new Dictionary<string, object> { ["op"] = index, ["key"] = key, ["addinVersion"] = ctx.Instance?.AddinVersion });
            }
            Dispatcher.CheckScope(ctx.Doc, meta, label);
            return new PlannedOp { Index = index, Key = key, Meta = meta, Binding = binding, RawArgs = args };
        }

        /// <summary>Runs the ops (in order, as one undo unit) and applies the blast/confirm/preview semantics.</summary>
        internal static OpResult Execute(RequestContext ctx, List<PlannedOp> ops, string groupName, bool isChangeSet, string setName)
        {
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document to write to.");
            DocumentRegistry registry = ctx.Registry;

            if (doc.IsWorkshared)
            {
                List<ElementId> inputs = CollectInputIds(ops);
                if (ctx.IsPreview)
                {
                    WorksharingGuard.Report report = WorksharingGuard.Inspect(doc, inputs);
                    if (report.NotProbed.Count > 0) return StaticPreview(ctx, ops, report, isChangeSet);
                }
                else
                {
                    WorksharingGuard.EnsureEditable(doc, inputs);
                }
            }

            var outcomes = new List<OpOutcome>(ops.Count);
            using (registry?.DeferStamps(doc))
            {
                if (ctx.IsPreview)
                {
                    using (var temp = new TempScope(doc, Naming.Purposes.Preview))
                    {
                        RunOps(ctx, ops, outcomes, isChangeSet, (action, policy) => temp.Run(action, policy));
                        Totals totals = Totals.Of(outcomes);
                        Blast blast = EvaluateBlast(ctx, outcomes, totals, isChangeSet);
                        long stamp = registry?.GetGeneration(doc) ?? 0;
                        return PreviewResult(ctx, outcomes, totals, blast, stamp, isChangeSet, setName);
                    }
                }

                using (var scope = new WriteScope(doc, groupName))
                {
                    RunOps(ctx, ops, outcomes, isChangeSet, (action, policy) => scope.RunTransaction(action, policy));
                    Totals totals = Totals.Of(outcomes);
                    Blast blast = EvaluateBlast(ctx, outcomes, totals, isChangeSet);

                    if (ctx.Confirmed != null)
                    {
                        OpResult stale = CheckStale(ctx, doc, totals);
                        if (stale != null)
                        {
                            scope.RollBack();
                            return stale;
                        }
                    }
                    else if (blast.Rule != null)
                    {
                        scope.RollBack();
                        long stamp = registry?.GetGeneration(doc) ?? 0;
                        return NotApplied(ctx, outcomes, totals, blast, stamp, isChangeSet, setName);
                    }

                    TransactionStatus status = scope.Assimilate();
                    if (status != TransactionStatus.Committed)
                    {
                        throw new OpException(ErrorCodes.RevitTransactionRolledBack, "Revit did not commit '" + groupName + "' (" + status + ").",
                            new Dictionary<string, object> { ["status"] = status.ToString() });
                    }
                    return Applied(ctx, outcomes, totals, isChangeSet, setName);
                }
            }
        }

        /// <summary>A ui op with tx "in" (isolate/hide/reset): one "MCP ui &lt;op&gt;" transaction, no group, no blast rules.</summary>
        private static OpResult RunUiOp(RequestContext ctx, PlannedOp op)
        {
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document for " + op.Key + ".");
            var c = new ChangeContext(ctx, new PayloadReader(op.RawArgs, null, ctx.Args.BareLengthToMm), op.Meta, 0);
            TxResult tx = McpTransactions.RunUi(doc, op.Meta.Op ?? op.Key, () => op.Binding.InvokeChange(c));
            if (!tx.Committed) throw tx.ToException(op.Key);
            foreach (CapturedFailure warning in tx.Warnings) c.Warn(WarningCodes.RevitWarning, warning.Text, warning.FailingIds.Select(id => new ElementId(id)));
            OpOutcome outcome = OpOutcome.From(op, c);
            var result = OpResult.Success(outcome.Data, outcome.Summary ?? (op.Key + " done"))
                .WithChanges(outcome.Created, outcome.Modified, outcome.Deleted.Select(d => new ElementId(d.Id)))
                .WithOutputs(outcome.Outputs);
            foreach (BridgeWarning warning in outcome.Warnings) OpResult.AddWarning(result.Warnings, warning.Code, warning.Text, warning.Ids, warning.N);
            return result;
        }

        private static void RunOps(RequestContext ctx, List<PlannedOp> ops, List<OpOutcome> outcomes, bool isChangeSet,
            Func<Action, FailurePolicy, TxResult> runTransaction)
        {
            foreach (List<PlannedOp> segment in PlanSegments(ops))
            {
                PlannedOp first = segment[0];
                if (first.Meta.Tx == "none")
                {
                    var run = new SegmentRun
                    {
                        Ctx = ctx,
                        Ops = segment,
                        IsChangeSet = isChangeSet,
                        CreateContext = op => CreateContext(ctx, op, outcomes, isChangeSet),
                        Complete = (op, c) => outcomes.Add(Finish(op, c, FailuresOf(c), isChangeSet))
                    };
                    try
                    {
                        RunFamilySegment(run);
                    }
                    catch (Exception ex)
                    {
                        throw Attribute(ex, first, isChangeSet, ctx);
                    }
                    continue;
                }

                foreach (PlannedOp op in segment)
                {
                    ChangeContext c = CreateContext(ctx, op, outcomes, isChangeSet);
                    TxResult tx = null;
                    try
                    {
                        if (op.Meta.Tx == "own")
                        {
                            op.Binding.InvokeChange(c);
                            if (c.Failures.RolledBack)
                            {
                                tx = new TxResult { Committed = false, Status = TransactionStatus.RolledBack };
                                tx.Errors.AddRange(c.Failures.Errors);
                                tx.Warnings.AddRange(c.Failures.Warnings);
                                throw tx.ToException(Label(op, isChangeSet));
                            }
                            tx = new TxResult { Committed = true, Status = TransactionStatus.Committed };
                            tx.Warnings.AddRange(c.Failures.Warnings);
                        }
                        else
                        {
                            FailurePolicy policy = op.Meta.Strict ? FailurePolicy.Strict : FailurePolicy.Default;
                            tx = runTransaction(() =>
                            {
                                op.Binding.InvokeChange(c);
                                policy.ResolveKnownErrors = c.ResolveErrors;
                                policy.Resolvable = c.ResolvableFailures;
                            }, policy);
                            if (!tx.Committed) throw tx.ToException(Label(op, isChangeSet));
                        }
                    }
                    catch (Exception ex)
                    {
                        throw Attribute(ex, op, isChangeSet, ctx);
                    }

                    if (op.Meta.Strict && tx.Warnings.Count > 0)
                    {
                        var failures = tx.Warnings.Select(w => (object)w.ToWire()).ToList();
                        throw new OpException(ErrorCodes.RevitTransactionRolledBack,
                            Label(op, isChangeSet) + ": Revit reported a warning, and this op rolls back on warnings: " + tx.Warnings[0].Text,
                            new Dictionary<string, object> { ["op"] = op.Index, ["key"] = op.Key, ["failures"] = failures, ["strict"] = true });
                    }
                    outcomes.Add(Finish(op, c, tx, isChangeSet));
                }
            }
        }

        private static ChangeContext CreateContext(RequestContext ctx, PlannedOp op, List<OpOutcome> outcomes, bool isChangeSet)
        {
            IDictionary<string, object> args = isChangeSet ? ResolveRefs(op.RawArgs, outcomes, op.Index) : op.RawArgs;
            string path = isChangeSet ? "ops[" + op.Index.ToString(CultureInfo.InvariantCulture) + "]" : null;
            return new ChangeContext(ctx, new PayloadReader(args, path, ctx.Args.BareLengthToMm), op.Meta, op.Index);
        }

        private static OpOutcome Finish(PlannedOp op, ChangeContext c, TxResult tx, bool isChangeSet)
        {
            string prefix = isChangeSet ? "[" + op.Index.ToString(CultureInfo.InvariantCulture) + "] " : string.Empty;
            if (tx != null)
            {
                foreach (CapturedFailure warning in tx.Warnings)
                {
                    OpResult.AddWarning(c.Warnings, WarningCodes.RevitWarning, warning.Text, warning.FailingIds.Concat(warning.AdditionalIds), 1);
                }
            }
            OpOutcome outcome = OpOutcome.From(op, c);
            if (isChangeSet)
            {
                foreach (BridgeWarning warning in outcome.Warnings) warning.Text = prefix + warning.Text;
            }
            return outcome;
        }

        /// <summary>Warnings captured by a handler-owned scope (tx own/none) as a committed TxResult.</summary>
        private static TxResult FailuresOf(ChangeContext c)
        {
            var tx = new TxResult { Committed = true, Status = TransactionStatus.Committed };
            tx.Warnings.AddRange(c.Failures.Warnings);
            return tx;
        }

        private static string Label(PlannedOp op, bool isChangeSet)
        {
            return isChangeSet ? "op [" + op.Index.ToString(CultureInfo.InvariantCulture) + "] " + op.Key : op.Key;
        }

        /// <summary>Adds the op index/key to an op failure and maps API exceptions.</summary>
        private static OpException Attribute(Exception ex, PlannedOp op, bool isChangeSet, RequestContext ctx)
        {
            OpException mapped = ex as OpException;
            if (mapped == null)
            {
                OpResult failure = Dispatcher.MapException(ctx, ex);
                mapped = new OpException(failure.Code, failure.Message, failure.Details);
            }
            if (!isChangeSet) return mapped;
            var details = new Dictionary<string, object>(StringComparer.Ordinal) { ["op"] = op.Index, ["key"] = op.Key };
            if (Wire.AsMap(mapped.Details) is IDictionary<string, object> original)
            {
                foreach (KeyValuePair<string, object> pair in original) if (!details.ContainsKey(pair.Key)) details[pair.Key] = pair.Value;
            }
            string prefix = "op [" + op.Index.ToString(CultureInfo.InvariantCulture) + "] " + op.Key + ": ";
            string message = mapped.Message.StartsWith("op [", StringComparison.Ordinal) || mapped.Message.StartsWith("ops[", StringComparison.Ordinal)
                ? mapped.Message
                : prefix + mapped.Message;
            return new OpException(mapped.Code, message, details);
        }

        // ---------------------------------------------------------------------------------------------------------
        // $refs ($N, $N.ids, $N.ids[k], $N.<output>, $prev..., $$literal)
        // ---------------------------------------------------------------------------------------------------------

        internal static IDictionary<string, object> ResolveRefs(IDictionary<string, object> args, List<OpOutcome> outcomes, int index)
        {
            var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in args ?? new Dictionary<string, object>())
            {
                resolved[pair.Key] = ResolveValue(pair.Value, outcomes, index, pair.Key, inArray: false);
            }
            return resolved;
        }

        private static object ResolveValue(object value, List<OpOutcome> outcomes, int index, string field, bool inArray)
        {
            switch (value)
            {
                case string text:
                    return ResolveString(text, outcomes, index, field);
                case IDictionary<string, object> map:
                {
                    var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object> pair in map) copy[pair.Key] = ResolveValue(pair.Value, outcomes, index, field + "." + pair.Key, false);
                    return copy;
                }
                default:
                    IList<object> list = Wire.AsList(value);
                    if (list == null) return value;
                    var items = new List<object>(list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        object item = ResolveValue(list[i], outcomes, index, field + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", true);
                        if (list[i] is string s && s.StartsWith("$", StringComparison.Ordinal) && !s.StartsWith("$$", StringComparison.Ordinal) && item is List<object> expanded)
                        {
                            items.AddRange(expanded);
                        }
                        else
                        {
                            items.Add(item);
                        }
                    }
                    return items;
            }
        }

        private static object ResolveString(string text, List<OpOutcome> outcomes, int index, string field)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '$') return text;
            if (text.StartsWith("$$", StringComparison.Ordinal)) return text.Substring(1);
            Match match = RefPattern.Match(text);
            string where = "ops[" + index.ToString(CultureInfo.InvariantCulture) + "]." + field;
            if (!match.Success)
            {
                throw OpException.InvalidArgs(where, "'" + text + "' is not a valid reference ($N, $N.ids, $N.ids[k], $N.<output>, $prev; use $$ for a literal $)");
            }
            int target = match.Groups["n"].Value == "prev" ? index - 1 : int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (target < 0 || target >= index)
            {
                throw OpException.InvalidArgs(where, "'" + text + "' must refer to an earlier op (0.." + (index - 1).ToString(CultureInfo.InvariantCulture) + ")");
            }
            OpOutcome outcome = outcomes.FirstOrDefault(o => o.Index == target);
            if (outcome == null) throw OpException.InvalidArgs(where, "'" + text + "' refers to op " + target.ToString(CultureInfo.InvariantCulture) + ", which produced no result");

            if (match.Groups["ids"].Success)
            {
                List<object> ids = outcome.Created.Select(id => (object)id.Value).ToList();
                if (!match.Groups["k"].Success) return ids;
                int k = int.Parse(match.Groups["k"].Value, CultureInfo.InvariantCulture);
                if (k >= ids.Count) throw OpException.InvalidArgs(where, "'" + text + "': op " + target.ToString(CultureInfo.InvariantCulture) + " created only " + ids.Count.ToString(CultureInfo.InvariantCulture) + " element(s)");
                return ids[k];
            }
            if (match.Groups["out"].Success)
            {
                string name = match.Groups["out"].Value;
                if (outcome.Outputs.TryGetValue(name, out object output)) return output;
                throw OpException.InvalidArgs(where, "'" + text + "': op " + target.ToString(CultureInfo.InvariantCulture) + " has no output '" + name + "'" +
                    (outcome.Outputs.Count > 0 ? " (outputs: " + string.Join(", ", outcome.Outputs.Keys) + ")" : string.Empty));
            }
            if (outcome.Primary == null) throw OpException.InvalidArgs(where, "'" + text + "': op " + target.ToString(CultureInfo.InvariantCulture) + " created or modified nothing");
            return outcome.Primary.Value;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Blast rules (SPEC §6.3) and results
        // ---------------------------------------------------------------------------------------------------------

        private sealed class Totals
        {
            public List<ElementId> Created = new List<ElementId>();
            public List<ElementId> Modified = new List<ElementId>();
            public List<DeletedRecord> Deleted = new List<DeletedRecord>();
            public int Affected;

            public List<long> CountedDeleteSet => Deleted.Where(d => d.Counted).Select(d => d.Id).Distinct().OrderBy(id => id).ToList();

            public static Totals Of(List<OpOutcome> outcomes)
            {
                var totals = new Totals();
                var created = new HashSet<long>();
                var deleted = new HashSet<long>();
                foreach (OpOutcome outcome in outcomes)
                {
                    foreach (ElementId id in outcome.Created) if (created.Add(id.Value)) totals.Created.Add(id);
                    foreach (DeletedRecord record in outcome.Deleted) if (deleted.Add(record.Id)) totals.Deleted.Add(record);
                    totals.Affected += outcome.Affected;
                }
                // Elements created and deleted inside the set are neither created nor deleted for the caller.
                totals.Created.RemoveAll(id => deleted.Contains(id.Value));
                totals.Deleted.RemoveAll(record => created.Contains(record.Id));
                var modified = new HashSet<long>();
                foreach (OpOutcome outcome in outcomes)
                {
                    foreach (ElementId id in outcome.Modified)
                    {
                        if (!created.Contains(id.Value) && !deleted.Contains(id.Value) && modified.Add(id.Value)) totals.Modified.Add(id);
                    }
                }
                return totals;
            }
        }

        private sealed class Blast
        {
            public string Rule;
            public object PendingPlan;
        }

        private static Blast EvaluateBlast(RequestContext ctx, List<OpOutcome> outcomes, Totals totals, bool isChangeSet)
        {
            ConfirmSettings confirm = ctx.Settings.Confirm;
            var blast = new Blast();
            foreach (OpOutcome outcome in outcomes)
            {
                int deleteCount = outcome.Deleted.Count(d => d.Counted);
                int modifyCount = outcome.Modified.Count + outcome.Affected;
                string rule =
                    outcome.Meta.HasBlast("delete") && deleteCount > confirm.DeleteOver ? "delete" :
                    outcome.Meta.HasBlast("bulk") && modifyCount > confirm.BulkOver ? "bulk" :
                    outcome.Meta.HasBlast("create") && outcome.Created.Count > confirm.CreateOver ? "create" :
                    outcome.PendingRule;
                if (rule != null)
                {
                    blast.Rule = rule;
                    blast.PendingPlan = rule == outcome.PendingRule ? outcome.PendingPlan : null;
                    return blast;
                }
            }

            if (isChangeSet)
            {
                OpMeta changeSet = ctx.Meta;
                int deleteTotal = totals.Deleted.Count(d => d.Counted);
                int modifyTotal = totals.Modified.Count + totals.Affected;
                if (changeSet.HasBlast("delete") && deleteTotal > confirm.DeleteOver) blast.Rule = "delete";
                else if (changeSet.HasBlast("bulk") && modifyTotal > confirm.BulkOver) blast.Rule = "bulk";
                else if (changeSet.HasBlast("create") && totals.Created.Count > confirm.CreateOver) blast.Rule = "create";
            }
            return blast;
        }

        private static OpResult CheckStale(RequestContext ctx, Document doc, Totals totals)
        {
            ConfirmedPlan confirmed = ctx.Confirmed;
            IEnumerable<ElementId> affected = totals.Modified.Concat(totals.Deleted.Select(d => new ElementId(d.Id)));
            List<long> changed = ctx.Registry?.ChangedSince(doc, affected, confirmed.Stamp) ?? new List<long>();
            if (changed.Count > 0)
            {
                return OpResult.Fail(ErrorCodes.ConfirmStale,
                    changed.Count.ToString(CultureInfo.InvariantCulture) + " element(s) of the plan changed after it was made; nothing was applied.",
                    new Dictionary<string, object> { ["changedIds"] = changed.Take(50).Cast<object>().ToList(), ["changedTotal"] = changed.Count, ["stamp"] = confirmed.Stamp });
            }

            if (confirmed.DeleteSet != null && confirmed.DeleteSet.Count > 0)
            {
                var expected = new HashSet<long>(confirmed.DeleteSet);
                List<long> actual = totals.CountedDeleteSet;
                if (!expected.SetEquals(actual))
                {
                    List<long> missing = expected.Except(actual).Take(20).ToList();
                    List<long> extra = actual.Where(id => !expected.Contains(id)).Take(20).ToList();
                    return OpResult.Fail(ErrorCodes.ConfirmStale,
                        "The delete set changed since the plan (planned " + expected.Count.ToString(CultureInfo.InvariantCulture) + ", now " +
                        actual.Count.ToString(CultureInfo.InvariantCulture) + " elements); nothing was applied.",
                        new Dictionary<string, object>
                        {
                            ["changedIds"] = missing.Concat(extra).Cast<object>().ToList(),
                            ["plannedTotal"] = expected.Count,
                            ["actualTotal"] = actual.Count
                        });
                }
            }
            return null;
        }

        private static BlastInfo BlastInfoOf(Document doc, Totals totals)
        {
            var info = new BlastInfo
            {
                ModifyTotal = totals.Modified.Count + totals.Affected,
                CreateTotal = totals.Created.Count
            };
            List<DeletedRecord> counted = totals.Deleted.Where(d => d.Counted).ToList();
            info.DeleteTotal = counted.Count;
            foreach (DeletedRecord record in counted)
            {
                string category = string.IsNullOrWhiteSpace(record.Category) ? "Other" : record.Category;
                info.ByCategory.TryGetValue(category, out int count);
                info.ByCategory[category] = count + 1;
            }
            info.Sample = counted.Select(d => d.Id).Take(50).ToList();
            return info;
        }

        private static object PlanOf(List<OpOutcome> outcomes, bool isChangeSet, object pendingPlan)
        {
            if (pendingPlan != null && !isChangeSet) return pendingPlan;
            if (!isChangeSet)
            {
                OpOutcome only = outcomes.FirstOrDefault();
                return new Dictionary<string, object>
                {
                    ["op"] = only?.Key,
                    ["summary"] = only?.Summary,
                    ["data"] = only?.Data
                };
            }
            return new Dictionary<string, object>
            {
                ["ops"] = outcomes.Select(o => (object)new List<object> { o.Index, o.Key, o.Summary ?? CountsText(o.Created.Count, o.Modified.Count + o.Affected, o.Deleted.Count) }).ToList()
            };
        }

        private static OpResult NotApplied(RequestContext ctx, List<OpOutcome> outcomes, Totals totals, Blast blast, long stamp, bool isChangeSet, string setName)
        {
            BlastInfo info = BlastInfoOf(ctx.Doc, totals);
            List<long> deleteSet = totals.CountedDeleteSet.Take(MaxDeleteSet).ToList();
            var needs = new NeedsConfirm
            {
                Rule = blast.Rule,
                Plan = PlanOf(outcomes, isChangeSet, blast.PendingPlan),
                Blast = info,
                Stamp = stamp,
                DeleteSet = deleteSet
            };
            var result = OpResult.Success(null, NotAppliedSummary(blast.Rule, info, totals, outcomes, isChangeSet, setName)).WithNeedsConfirm(needs);
            foreach (OpOutcome outcome in outcomes)
            {
                foreach (BridgeWarning warning in outcome.Warnings) OpResult.AddWarning(result.Warnings, warning.Code, warning.Text, warning.Ids, warning.N);
            }
            return result;
        }

        private static string NotAppliedSummary(string rule, BlastInfo info, Totals totals, List<OpOutcome> outcomes, bool isChangeSet, string setName)
        {
            string what = isChangeSet ? "change_set" + (string.IsNullOrWhiteSpace(setName) ? string.Empty : " " + setName) : outcomes.FirstOrDefault()?.Key ?? "write";
            switch (rule)
            {
                case "delete":
                    int targets = Math.Min(info.DeleteTotal, CountTargets(outcomes));
                    return what + " would delete " + info.DeleteTotal.ToString(CultureInfo.InvariantCulture) + " elements" +
                           (targets > 0 && targets < info.DeleteTotal
                               ? " (" + targets.ToString(CultureInfo.InvariantCulture) + " targets + " + (info.DeleteTotal - targets).ToString(CultureInfo.InvariantCulture) + " dependents)"
                               : string.Empty);
                case "bulk":
                    return what + " would modify " + info.ModifyTotal.ToString(CultureInfo.InvariantCulture) + " existing elements";
                case "create":
                    return what + " would create " + info.CreateTotal.ToString(CultureInfo.InvariantCulture) + " elements";
                default:
                    return what + " needs confirmation (" + rule + ")";
            }
        }

        private static int CountTargets(List<OpOutcome> outcomes)
        {
            return outcomes.SelectMany(o => o.Deleted).Where(d => d.Counted && d.Target).Select(d => d.Id).Distinct().Count();
        }

        private static OpResult PreviewResult(RequestContext ctx, List<OpOutcome> outcomes, Totals totals, Blast blast, long stamp, bool isChangeSet, string setName)
        {
            BlastInfo info = BlastInfoOf(ctx.Doc, totals);
            var needs = new NeedsConfirm
            {
                Rule = blast.Rule ?? "preview",
                Plan = PlanOf(outcomes, isChangeSet, blast.PendingPlan),
                Blast = info,
                Stamp = stamp,
                DeleteSet = totals.CountedDeleteSet.Take(MaxDeleteSet).ToList()
            };
            int warningCount = outcomes.Sum(o => o.Warnings.Sum(w => Math.Max(1, w.N)));
            var data = new Dictionary<string, object>
            {
                ["preview"] = true,
                ["plan"] = needs.Plan,
                ["counts"] = new Dictionary<string, object> { ["create"] = totals.Created.Count, ["modify"] = info.ModifyTotal, ["delete"] = info.DeleteTotal },
                ["delete_set"] = new Dictionary<string, object>
                {
                    ["total"] = info.DeleteTotal,
                    ["by_category"] = info.ByCategory.ToDictionary(pair => pair.Key, pair => (object)pair.Value),
                    ["sample"] = info.Sample.Cast<object>().ToList()
                },
                ["expected_warnings"] = warningCount
            };
            if (blast.Rule != null) data["rule"] = blast.Rule;
            string what = isChangeSet ? "change_set" + (string.IsNullOrWhiteSpace(setName) ? string.Empty : " " + setName) : outcomes.FirstOrDefault()?.Key ?? "write";
            var result = OpResult.Success(data, "preview of " + what + ": would " + CountsText(totals.Created.Count, info.ModifyTotal, info.DeleteTotal) + "; nothing was changed")
                .WithNeedsConfirm(needs);
            foreach (OpOutcome outcome in outcomes)
            {
                foreach (BridgeWarning warning in outcome.Warnings) OpResult.AddWarning(result.Warnings, warning.Code, warning.Text, warning.Ids, warning.N);
            }
            return result;
        }

        private static OpResult StaticPreview(RequestContext ctx, List<PlannedOp> ops, WorksharingGuard.Report report, bool isChangeSet)
        {
            string owners = report.Owners.Count == 0 ? "nobody (not borrowed)" : string.Join(", ", report.Owners);
            var data = new Dictionary<string, object>
            {
                ["preview"] = true,
                ["static"] = true,
                ["not_probed"] = report.NotProbed.Count,
                ["owners"] = report.Owners.Cast<object>().ToList(),
                ["plan"] = ops.Select(op => (object)new List<object> { op.Index, op.Key }).ToList()
            };
            var needs = new NeedsConfirm
            {
                Rule = "preview",
                Plan = data["plan"],
                Stamp = ctx.Registry?.GetGeneration(ctx.Doc) ?? 0
            };
            return OpResult.Success(data,
                    "preview (static validation only): not probed: " + report.NotProbed.Count.ToString(CultureInfo.InvariantCulture) +
                    " elements owned by " + owners + "; nothing was changed")
                .WithNeedsConfirm(needs);
        }

        private static OpResult Applied(RequestContext ctx, List<OpOutcome> outcomes, Totals totals, bool isChangeSet, string setName)
        {
            OpResult result;
            if (isChangeSet)
            {
                var data = new Dictionary<string, object>
                {
                    ["ops"] = outcomes.Select(o => (object)new List<object>
                    {
                        o.Index, o.Key, o.Primary?.Value, o.Created.Count + o.Modified.Count + o.Deleted.Count
                    }).ToList()
                };
                var outputs = outcomes.Where(o => o.Outputs.Count > 0)
                    .ToDictionary(o => o.Index.ToString(CultureInfo.InvariantCulture), o => (object)o.Outputs);
                if (outputs.Count > 0) data["outputs"] = outputs;
                string name = string.IsNullOrWhiteSpace(setName) ? string.Empty : " " + setName;
                result = OpResult.Success(data, "change_set" + name + ": " + outcomes.Count.ToString(CultureInfo.InvariantCulture) + " ops; " +
                    CountsText(totals.Created.Count, totals.Modified.Count, totals.Deleted.Count));
            }
            else
            {
                OpOutcome only = outcomes[0];
                result = OpResult.Success(only.Data, only.Summary ?? AutoSummary(ctx.Doc, totals)).WithOutputs(only.Outputs);
            }

            result.WithChanges(totals.Created, totals.Modified, totals.Deleted.Select(d => new ElementId(d.Id)));
            foreach (OpOutcome outcome in outcomes)
            {
                foreach (BridgeWarning warning in outcome.Warnings) OpResult.AddWarning(result.Warnings, warning.Code, warning.Text, warning.Ids, warning.N);
            }
            return result;
        }

        private static string AutoSummary(Document doc, Totals totals)
        {
            if (totals.Created.Count == 0 && totals.Modified.Count == 0 && totals.Deleted.Count == 0) return "no changes";
            var parts = new List<string>();
            if (totals.Created.Count > 0) parts.Add("created " + Describe(doc, totals.Created));
            if (totals.Modified.Count > 0) parts.Add("modified " + Describe(doc, totals.Modified));
            if (totals.Deleted.Count > 0) parts.Add("deleted " + totals.Deleted.Count.ToString(CultureInfo.InvariantCulture) + " element" + (totals.Deleted.Count == 1 ? string.Empty : "s"));
            return string.Join(", ", parts);
        }

        /// <summary>"4 Walls" when all share a category, else "6 elements".</summary>
        private static string Describe(Document doc, List<ElementId> ids)
        {
            string count = ids.Count.ToString(CultureInfo.InvariantCulture);
            try
            {
                var labels = new HashSet<string>(StringComparer.Ordinal);
                foreach (ElementId id in ids.Take(2000))
                {
                    labels.Add(Rows.CategoryLabel(doc.GetElement(id)?.Category) ?? "element");
                    if (labels.Count > 1) break;
                }
                if (labels.Count == 1)
                {
                    string label = labels.First();
                    return count + " " + (ids.Count == 1 ? Singular(label) : label);
                }
            }
            catch
            {
                // Fall back to the generic text.
            }
            return count + " element" + (ids.Count == 1 ? string.Empty : "s");
        }

        private static string Singular(string label)
        {
            if (label.EndsWith("ies", StringComparison.Ordinal)) return label.Substring(0, label.Length - 3) + "y";
            if (label.EndsWith("s", StringComparison.Ordinal) && !label.EndsWith("ss", StringComparison.Ordinal)) return label.Substring(0, label.Length - 1);
            return label;
        }

        private static string CountsText(int created, int modified, int deleted)
        {
            var parts = new List<string>();
            if (created > 0) parts.Add("create " + created.ToString(CultureInfo.InvariantCulture));
            if (modified > 0) parts.Add("modify " + modified.ToString(CultureInfo.InvariantCulture));
            if (deleted > 0) parts.Add("delete " + deleted.ToString(CultureInfo.InvariantCulture));
            return parts.Count == 0 ? "change nothing" : string.Join(", ", parts);
        }

        /// <summary>Literal element ids among the ops' inputs (ids/id/element(s)/host/target), $refs excluded.</summary>
        private static List<ElementId> CollectInputIds(List<PlannedOp> ops)
        {
            var ids = new HashSet<long>();
            foreach (PlannedOp op in ops)
            {
                var reader = new PayloadReader(op.RawArgs, null, 1.0);
                foreach (string key in new[] { "ids", "id", "element", "elements", "host", "target", "targets" })
                {
                    if (!reader.Has(key)) continue;
                    try
                    {
                        var others = new List<string>();
                        foreach (long id in reader.IdValues(key, others)) ids.Add(id);
                    }
                    catch (OpException)
                    {
                        // Not ids (e.g. names); the op validates its own args.
                    }
                }
            }
            return ids.Select(id => new ElementId(id)).ToList();
        }
    }
}
