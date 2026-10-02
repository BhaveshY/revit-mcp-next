using System;
using System.Collections.Generic;
using System.Globalization;

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

    // SPEC §6.7 family segments. This file belongs to P-FAMILY in wave 2; the engine only calls PlanSegments and
    // RunFamilySegment. Wave-1 plumbing: every tx "none" op is its own segment and runs its handler between the
    // project transactions (the handler opens/edits/loads the family itself). P-FAMILY makes consecutive edit_family
    // ops on the same family= share one EditFamily session (one famDoc Transaction per op, then LoadFamily with no
    // project Transaction open, then Close(false)), and moves family segments last if spike V6 fails.
    internal static partial class ChangeEngine
    {
        /// <summary>Splits the ops into segments: tx in/own ops one per segment; tx none ops grouped per family session.</summary>
        internal static List<List<PlannedOp>> PlanSegments(List<PlannedOp> ops)
        {
            var segments = new List<List<PlannedOp>>(ops.Count);
            foreach (PlannedOp op in ops)
            {
                segments.Add(new List<PlannedOp> { op });
            }
            return segments;
        }

        /// <summary>Runs one segment of tx "none" ops between transactions (no project Transaction is open).</summary>
        internal static void RunFamilySegment(SegmentRun run)
        {
            foreach (PlannedOp op in run.Ops)
            {
                ChangeContext c = run.CreateContext(op);
                op.Binding.InvokeChange(c);
                if (c.Failures.RolledBack)
                {
                    var tx = new TxResult { Committed = false, Status = Autodesk.Revit.DB.TransactionStatus.RolledBack };
                    tx.Errors.AddRange(c.Failures.Errors);
                    tx.Warnings.AddRange(c.Failures.Warnings);
                    throw tx.ToException(run.IsChangeSet ? "op [" + op.Index.ToString(CultureInfo.InvariantCulture) + "] " + op.Key : op.Key);
                }
                run.Complete(op, c);
            }
        }
    }
}
