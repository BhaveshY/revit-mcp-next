using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private sealed class PreviewProbeFailurePreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                bool hasError = false;
                foreach (FailureMessageAccessor failure in failures)
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(failure);
                        continue;
                    }

                    hasError = true;
                }

                return hasError
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue;
            }
        }

        private static void ConfigurePreviewProbeTransaction(Transaction transaction)
        {
            var preprocessor = new PreviewProbeFailurePreprocessor();
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            options.SetFailuresPreprocessor(preprocessor);
            transaction.SetFailureHandlingOptions(options);
        }

        private static void RollBackPreviewProbeTransaction(Transaction transaction)
        {
            try
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                {
                    transaction.RollBack();
                }
            }
            catch
            {
                // Preview probes are best-effort validation. If Revit is already unwinding
                // a failed transaction, preserve the original preview error.
            }
        }

        private static void RollBackPreviewProbeSubTransaction(SubTransaction subTransaction, bool started)
        {
            if (!started) return;
            try
            {
                subTransaction.RollBack();
            }
            catch
            {
                // Preserve the original preview error if Revit already unwound the subtransaction.
            }
        }

        private static string FormatPreviewProbeError(string operationType, Exception exception)
        {
            string message = exception == null ? string.Empty : exception.Message;
            if (string.IsNullOrWhiteSpace(message)) message = exception == null ? "Unknown Revit API error." : exception.GetType().Name;
            message = message.Replace("\r", " ").Replace("\n", " ").Trim();
            return "Revit rejected " + operationType + " preview: " + message;
        }

        private static string FormatElementIdList(IEnumerable<string> ids, int max = 12)
        {
            List<string> values = (ids ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Take(max + 1)
                .ToList();
            if (values.Count <= max) return string.Join(", ", values);
            values = values.Take(max).ToList();
            return string.Join(", ", values) + ", ...";
        }
    }
}
