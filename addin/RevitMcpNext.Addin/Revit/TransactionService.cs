using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class TransactionService
    {
        public T Read<T>(Func<T> action)
        {
            return action();
        }

        public T Write<T>(Document document, string name, Func<T> action)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Transaction name is required.", nameof(name));

            using (var transaction = new Transaction(document, name))
            {
                TransactionStatus startStatus = transaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("Revit transaction '" + name + "' could not start. Status: " + startStatus + ".");
                }

                var preprocessor = new RollbackOnFailurePreprocessor();
                FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                options.SetFailuresPreprocessor(preprocessor);
                transaction.SetFailureHandlingOptions(options);

                try
                {
                    T result = action();
                    TransactionStatus commitStatus = transaction.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException(
                            "Revit transaction '" + name + "' did not commit. Status: " + commitStatus + FormatFailures(preprocessor.FailureMessages) + ".");
                    }

                    return result;
                }
                catch
                {
                    RollBackIfStarted(transaction);
                    throw;
                }
            }
        }

        private static void RollBackIfStarted(Transaction transaction)
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
            {
                transaction.RollBack();
            }
        }

        private static string FormatFailures(IReadOnlyCollection<string> failureMessages)
        {
            if (failureMessages == null || failureMessages.Count == 0) return string.Empty;
            return " Failures: " + string.Join("; ", failureMessages);
        }

        private sealed class RollbackOnFailurePreprocessor : IFailuresPreprocessor
        {
            private readonly List<string> _failureMessages = new List<string>();

            public IReadOnlyCollection<string> FailureMessages => _failureMessages;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                bool hasError = false;

                foreach (FailureMessageAccessor failure in failures)
                {
                    FailureSeverity severity = failure.GetSeverity();
                    if (severity == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(failure);
                        continue;
                    }

                    hasError = true;
                    string description = failure.GetDescriptionText();
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        _failureMessages.Add(description);
                    }
                }

                return hasError
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue;
            }
        }
    }
}
