using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMcpNext.Addin
{
    /// <summary>One run of consecutive tx "none" ops handed to <see cref="ChangeEngine.RunFamilySegment"/>.</summary>
    internal sealed class SegmentRun
    {
        public RequestContext Ctx { get; set; }
        /// <summary>The ops of the segment, in order.</summary>
        public List<PlannedOp> Ops { get; set; } = new List<PlannedOp>();
        public bool IsChangeSet { get; set; }
        /// <summary>Resolves the op's $refs against earlier outcomes and builds its ChangeContext (call once per op, in order).</summary>
        public Func<PlannedOp, ChangeContext> CreateContext { get; set; }
        /// <summary>Records the op's outcome (call once per op, in order, after it succeeded).</summary>
        public Action<PlannedOp, ChangeContext> Complete { get; set; }
    }

    // SPEC §6.7 family segments (owned by P-FAMILY). Consecutive edit_family ops on the same family= (same reload and
    // overwrite) share one FamilySession: one EditFamily copy, one famDoc Transaction per op, then after the last op
    // famDoc.LoadFamily(host) with no host Transaction open (the engine's WriteScope group stays open around it, so a
    // change_set stays one undo step), then Close(false). Direct edits of an open family document share the session too.
    // Other tx "none" ops (edit_family.load_into) run on their own between the transactions.
    internal static partial class ChangeEngine
    {
        /// <summary>
        /// Spike V6 fallback switch: when a TransactionGroup rollback does not revert famDoc.LoadFamily, family segments
        /// must run after every other op of a change set. Off until V6 is measured on 2024 and 2027.
        /// </summary>
        internal const bool FamilySegmentsLast = false;

        /// <summary>edit_family ops that edit a family document through a <see cref="FamilySession"/>.</summary>
        private static readonly HashSet<string> FamilyEditKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "edit_family.add_param",
            "edit_family.remove_param",
            "edit_family.rename_param",
            "edit_family.set_param",
            "edit_family.set_formula",
            "edit_family.set_values",
            "edit_family.add_type",
            "edit_family.rename_type",
            "edit_family.delete_type",
            "edit_family.set_category"
        };

        internal static bool IsFamilyEditKey(string key) => key != null && FamilyEditKeys.Contains(key);

        /// <summary>Splits the ops into segments: tx in/own ops one per segment; edit_family ops grouped per family session.</summary>
        internal static List<List<PlannedOp>> PlanSegments(List<PlannedOp> ops)
        {
            var segments = new List<List<PlannedOp>>(ops.Count);
            List<PlannedOp> current = null;
            string currentKey = null;
            foreach (PlannedOp op in ops)
            {
                string key = op.Meta?.Tx == "none" ? SessionKey(op) : null;
                if (key != null && current != null && key == currentKey)
                {
                    current.Add(op);
                    continue;
                }
                current = new List<PlannedOp> { op };
                currentKey = key;
                segments.Add(current);
            }
            return FamilySegmentsLast ? MoveFamilySegmentsLast(segments) : segments;
        }

        /// <summary>
        /// Grouping key of an edit_family op: the literal family= (case-insensitive) plus reload and overwrite. Null for
        /// ops that never share a session (other keys, $ref family values).
        /// </summary>
        private static string SessionKey(PlannedOp op)
        {
            if (!IsFamilyEditKey(op.Key)) return null;
            var reader = new PayloadReader(op.RawArgs, null, 1.0);
            string family;
            string reload;
            string overwrite;
            try
            {
                family = reader.Str("family");
                reload = reader.Has("reload") ? (reader.BoolOpt("reload") == false ? "0" : "1") : "1";
                overwrite = reader.Has("overwrite") ? (reader.BoolOpt("overwrite") == true ? "1" : "0") : "-";
            }
            catch (OpException)
            {
                return null;
            }
            if (family != null && family.StartsWith("$", StringComparison.Ordinal) && !family.StartsWith("$$", StringComparison.Ordinal)) return null;
            return "family=" + (family ?? string.Empty).Trim().ToLowerInvariant() + "|reload=" + reload + "|overwrite=" + overwrite;
        }

        /// <summary>V6 fallback: family segments after the others, unless an op refers to a family op's result.</summary>
        private static List<List<PlannedOp>> MoveFamilySegmentsLast(List<List<PlannedOp>> segments)
        {
            var family = segments.Where(s => IsFamilyEditKey(s[0].Key)).ToList();
            if (family.Count == 0 || family.Count == segments.Count) return segments;
            var familyIndexes = new HashSet<int>(family.SelectMany(s => s).Select(op => op.Index));
            bool referenced = segments.Where(s => !IsFamilyEditKey(s[0].Key)).SelectMany(s => s)
                .Any(op => familyIndexes.Any(i => i < op.Index && RefersTo(op.RawArgs, i)));
            if (referenced) return segments;
            return segments.Where(s => !IsFamilyEditKey(s[0].Key)).Concat(family).ToList();
        }

        private static bool RefersTo(object value, int index)
        {
            switch (value)
            {
                case string text:
                    return text.StartsWith("$" + index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) || text.StartsWith("$prev", StringComparison.Ordinal);
                case IDictionary<string, object> map:
                    return map.Values.Any(v => RefersTo(v, index));
                default:
                    IList<object> list = RevitMcpNext.Contracts.Wire.AsList(value);
                    return list != null && list.Any(v => RefersTo(v, index));
            }
        }

        /// <summary>Runs one segment of tx "none" ops between transactions (no project Transaction is open).</summary>
        internal static void RunFamilySegment(SegmentRun run)
        {
            if (!IsFamilyEditKey(run.Ops[0].Key))
            {
                foreach (PlannedOp op in run.Ops) RunStandalone(run, op);
                return;
            }

            RequestContext ctx = run.Ctx;
            ChangeContext first = run.CreateContext(run.Ops[0]);
            object previous = ctx.Items.TryGetValue(FamilySession.ItemKey, out object existing) ? existing : null;
            using (FamilySession session = FamilySession.Open(first))
            {
                ctx.Items[FamilySession.ItemKey] = session;
                try
                {
                    for (int i = 0; i < run.Ops.Count; i++)
                    {
                        PlannedOp op = run.Ops[i];
                        ChangeContext c = i == 0 ? first : run.CreateContext(op);
                        op.Binding.InvokeChange(c);
                        ThrowIfRolledBack(run, op, c);
                        if (i == run.Ops.Count - 1)
                        {
                            session.Finish(c);
                            ThrowIfRolledBack(run, op, c);
                        }
                        run.Complete(op, c);
                    }
                }
                finally
                {
                    if (previous != null) ctx.Items[FamilySession.ItemKey] = previous;
                    else ctx.Items.Remove(FamilySession.ItemKey);
                }
            }
        }

        private static void RunStandalone(SegmentRun run, PlannedOp op)
        {
            ChangeContext c = run.CreateContext(op);
            op.Binding.InvokeChange(c);
            ThrowIfRolledBack(run, op, c);
            run.Complete(op, c);
        }

        private static void ThrowIfRolledBack(SegmentRun run, PlannedOp op, ChangeContext c)
        {
            if (!c.Failures.RolledBack) return;
            var tx = new TxResult { Committed = false, Status = Autodesk.Revit.DB.TransactionStatus.RolledBack };
            tx.Errors.AddRange(c.Failures.Errors);
            tx.Warnings.AddRange(c.Failures.Warnings);
            throw tx.ToException(run.IsChangeSet ? "op [" + op.Index.ToString(CultureInfo.InvariantCulture) + "] " + op.Key : op.Key);
        }
    }
}
