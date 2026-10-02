using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin
{
    /// <summary>A Revit failure message captured by <see cref="FailureCapture"/>.</summary>
    internal sealed class CapturedFailure
    {
        /// <summary>warning | error.</summary>
        public string Severity { get; set; } = "warning";
        public string Text { get; set; } = string.Empty;
        public Guid DefinitionId { get; set; }
        /// <summary>At most 20 failing element ids.</summary>
        public List<long> FailingIds { get; set; } = new List<long>();
        /// <summary>At most 20 additional element ids.</summary>
        public List<long> AdditionalIds { get; set; } = new List<long>();
        public bool HasResolutions { get; set; }
        public bool Resolved { get; set; }

        public Dictionary<string, object> ToWire()
        {
            return new Dictionary<string, object>
            {
                ["severity"] = Severity,
                ["text"] = Text,
                ["failure"] = DefinitionId.ToString(),
                ["ids"] = FailingIds.Concat(AdditionalIds).Distinct().Take(20).Cast<object>().ToList(),
                ["resolved"] = Resolved
            };
        }

        internal static CapturedFailure From(FailureMessageAccessor failure)
        {
            var record = new CapturedFailure
            {
                Severity = failure.GetSeverity() == FailureSeverity.Warning ? "warning" : "error"
            };
            try { record.Text = failure.GetDescriptionText() ?? string.Empty; } catch { }
            try { record.DefinitionId = failure.GetFailureDefinitionId()?.Guid ?? Guid.Empty; } catch { }
            try { record.FailingIds = (failure.GetFailingElementIds() ?? new List<ElementId>()).Take(20).Select(id => id.Value).ToList(); } catch { }
            try { record.AdditionalIds = (failure.GetAdditionalElementIds() ?? new List<ElementId>()).Take(20).Select(id => id.Value).ToList(); } catch { }
            try { record.HasResolutions = failure.HasResolutions(); } catch { }
            return record;
        }
    }

    /// <summary>How a transaction treats failures.</summary>
    internal sealed class FailurePolicy
    {
        /// <summary>Strict ops: warnings roll the transaction back too.</summary>
        public bool FailOnWarnings { get; set; }

        /// <summary>Resolve error failures that have a default resolution (only those accepted by <see cref="Resolvable"/>).</summary>
        public bool ResolveKnownErrors { get; set; }

        /// <summary>Failure definition ids that may be resolved automatically; null accepts all resolvable errors.</summary>
        public Func<Guid, bool> Resolvable { get; set; }

        public static FailurePolicy Default => new FailurePolicy();

        public static FailurePolicy Strict => new FailurePolicy { FailOnWarnings = true };
    }

    /// <summary>
    /// IFailuresPreprocessor that records every failure (D2 §7.1, D3 §2.8): warnings are deleted from the accessor so no
    /// dialog appears, but they are kept and returned as REVIT_WARNING; errors roll the transaction back unless a known
    /// resolution was allowed. Use <see cref="Configure"/> on every MCP transaction (it also sets the D2 §7.2 options).
    /// </summary>
    internal sealed class FailureCapture : IFailuresPreprocessor
    {
        private readonly FailurePolicy _policy;

        public FailureCapture(FailurePolicy policy = null)
        {
            _policy = policy ?? FailurePolicy.Default;
        }

        public List<CapturedFailure> Warnings { get; } = new List<CapturedFailure>();
        public List<CapturedFailure> Errors { get; } = new List<CapturedFailure>();

        /// <summary>True when the last processing pass decided to roll back.</summary>
        public bool RolledBack { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error = false;
            bool resolved = false;
            try
            {
                foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
                {
                    CapturedFailure record = CapturedFailure.From(failure);
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        Warnings.Add(record);
                        if (_policy.FailOnWarnings) error = true;
                        else accessor.DeleteWarning(failure);
                    }
                    else
                    {
                        Errors.Add(record);
                        bool canResolve = _policy.ResolveKnownErrors && record.HasResolutions &&
                                          (_policy.Resolvable == null || _policy.Resolvable(record.DefinitionId));
                        if (canResolve)
                        {
                            accessor.ResolveFailure(failure);
                            record.Resolved = true;
                            resolved = true;
                        }
                        else
                        {
                            error = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("failures", "Failure preprocessing failed; rolling back.", ex);
                error = true;
            }

            RolledBack = error;
            if (error) return FailureProcessingResult.ProceedWithRollBack;
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }

        /// <summary>
        /// Applies this preprocessor and the D2 §7.2 options (SetClearAfterRollback(true), SetForcedModalHandling(false),
        /// SetDelayedMiniWarnings(false)) to a transaction. Call before Start().
        /// </summary>
        public static void Configure(Transaction transaction, FailureCapture capture)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(capture);
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            options.SetDelayedMiniWarnings(false);
            transaction.SetFailureHandlingOptions(options);
        }

        /// <summary>The captured warnings as wire warnings (deduplicated by text).</summary>
        public void CopyWarningsTo(List<RevitMcpNext.Contracts.BridgeWarning> target, string prefix = null)
        {
            foreach (CapturedFailure warning in Warnings)
            {
                OpResult.AddWarning(target, WarningCodes.RevitWarning, (prefix ?? string.Empty) + warning.Text,
                    warning.FailingIds.Concat(warning.AdditionalIds), 1);
            }
        }

        /// <summary>
        /// Global Application.FailuresProcessing safety net (D2 §7.3): acts only while an MCP item executes. Warnings
        /// are recorded on the current request and deleted; errors in transactions named "MCP ..." roll back; any
        /// other error is left to Revit.
        /// </summary>
        public static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs args)
        {
            try
            {
                RequestContext ctx = RequestContext.Current;
                if (ctx == null) return;
                FailuresAccessor accessor = args.GetFailuresAccessor();
                string transactionName = string.Empty;
                try { transactionName = accessor.GetTransactionName() ?? string.Empty; } catch { }
                bool ours = Naming.IsMcpName(transactionName);
                bool rollback = false;
                foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
                {
                    CapturedFailure record = CapturedFailure.From(failure);
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        ctx.RevitWarnings.Add(record);
                        accessor.DeleteWarning(failure);
                    }
                    else if (ours)
                    {
                        ctx.RevitErrors.Add(record);
                        rollback = true;
                    }
                }
                if (rollback) args.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("failures", "FailuresProcessing safety net failed.", ex);
            }
        }
    }
}
