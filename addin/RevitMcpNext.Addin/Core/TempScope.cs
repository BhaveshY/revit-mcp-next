using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin
{
    /// <summary>Outcome of one transaction run by a scope.</summary>
    internal sealed class TxResult
    {
        public bool Committed { get; set; }
        public TransactionStatus Status { get; set; }
        public List<CapturedFailure> Warnings { get; set; } = new List<CapturedFailure>();
        public List<CapturedFailure> Errors { get; set; } = new List<CapturedFailure>();

        /// <summary>REVIT_TRANSACTION_ROLLED_BACK with the failures (call only when !Committed).</summary>
        public OpException ToException(string what)
        {
            var failures = new List<object>();
            foreach (CapturedFailure failure in Errors) failures.Add(failure.ToWire());
            foreach (CapturedFailure failure in Warnings) failures.Add(failure.ToWire());
            string first = Errors.Count > 0 ? Errors[0].Text : (Warnings.Count > 0 ? Warnings[0].Text : "status " + Status);
            return new OpException(
                ErrorCodes.RevitTransactionRolledBack,
                (string.IsNullOrEmpty(what) ? string.Empty : what + ": ") + "Revit rolled the change back: " + first,
                new Dictionary<string, object> { ["failures"] = failures, ["status"] = Status.ToString() });
        }
    }

    /// <summary>
    /// A TransactionGroup that is always rolled back (SPEC §6.2, §10.1): previews, captures, probes. Inner transactions
    /// are named "MCP temp &lt;purpose&gt;", so DocumentRegistry ignores them (no generation bump, no stamps, no ring).
    /// When the document already has an open transaction the scope uses a SubTransaction instead (rolled back too).
    /// Not thread-safe; use on the UI thread only.
    /// </summary>
    internal sealed class TempScope : IDisposable
    {
        [ThreadStatic] private static List<TempScope> _stack;

        private readonly TransactionGroup _group;
        private readonly SubTransaction _sub;
        private bool _disposed;

        public TempScope(Document document, string purpose)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Purpose = string.IsNullOrWhiteSpace(purpose) ? Naming.Purposes.Probe : purpose;
            Name = Naming.Temp(Purpose);
            Push(this);
            try
            {
                if (document.IsModifiable)
                {
                    _sub = new SubTransaction(document);
                    if (_sub.Start() != TransactionStatus.Started)
                    {
                        throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start a temporary sub-transaction.");
                    }
                }
                else
                {
                    _group = new TransactionGroup(document, Name);
                    TransactionStatus status = _group.Start();
                    if (status != TransactionStatus.Started)
                    {
                        throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start the temporary transaction group (" + status + ").");
                    }
                }
            }
            catch
            {
                Pop(this);
                _group?.Dispose();
                _sub?.Dispose();
                throw;
            }
        }

        /// <summary>True while any TempScope is open on this thread.</summary>
        public static bool IsActive => _stack != null && _stack.Count > 0;

        /// <summary>The innermost open TempScope on this thread, or null.</summary>
        public static TempScope Active => IsActive ? _stack[_stack.Count - 1] : null;

        public Document Document { get; }
        public string Purpose { get; }
        /// <summary>The group/transaction name, "MCP temp &lt;purpose&gt;".</summary>
        public string Name { get; }

        /// <summary>All failures captured by transactions of this scope.</summary>
        public List<CapturedFailure> Warnings { get; } = new List<CapturedFailure>();
        public List<CapturedFailure> Errors { get; } = new List<CapturedFailure>();

        /// <summary>
        /// Runs <paramref name="action"/> in an inner transaction named like the scope and commits it (inside the group).
        /// Exceptions roll the inner transaction back and propagate. In SubTransaction mode the action runs directly.
        /// </summary>
        public TxResult Run(Action action, FailurePolicy policy = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TempScope));
            if (_sub != null)
            {
                action();
                return new TxResult { Committed = true, Status = TransactionStatus.Committed };
            }

            var capture = new FailureCapture(policy);
            var result = new TxResult();
            using (var transaction = new Transaction(Document, Name))
            {
                FailureCapture.Configure(transaction, capture);
                TransactionStatus start = transaction.Start();
                if (start != TransactionStatus.Started)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start a temporary transaction (" + start + ").");
                }
                try
                {
                    action();
                    result.Status = transaction.Commit();
                }
                catch
                {
                    RollBackQuietly(transaction);
                    Collect(capture, result);
                    throw;
                }
            }
            Collect(capture, result);
            result.Committed = result.Status == TransactionStatus.Committed;
            return result;
        }

        /// <summary>Runs a function in an inner transaction (see <see cref="Run(Action, FailurePolicy)"/>).</summary>
        public T Run<T>(Func<T> action, out TxResult result, FailurePolicy policy = null)
        {
            T value = default(T);
            result = Run(() => { value = action(); }, policy);
            return value;
        }

        /// <summary>Rolls everything back (always).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_sub != null)
                {
                    if (_sub.GetStatus() == TransactionStatus.Started) _sub.RollBack();
                    _sub.Dispose();
                }
                if (_group != null)
                {
                    if (_group.GetStatus() == TransactionStatus.Started) _group.RollBack();
                    _group.Dispose();
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("tx", "Rolling back temporary scope '" + Name + "' failed.", ex);
            }
            finally
            {
                Pop(this);
            }
        }

        private void Collect(FailureCapture capture, TxResult result)
        {
            result.Warnings.AddRange(capture.Warnings);
            result.Errors.AddRange(capture.Errors);
            Warnings.AddRange(capture.Warnings);
            Errors.AddRange(capture.Errors);
        }

        internal static void RollBackQuietly(Transaction transaction)
        {
            try
            {
                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            }
            catch
            {
                // The transaction may already be finished by Revit's failure processing.
            }
        }

        private static void Push(TempScope scope)
        {
            if (_stack == null) _stack = new List<TempScope>();
            _stack.Add(scope);
        }

        private static void Pop(TempScope scope)
        {
            if (_stack == null) return;
            int index = _stack.LastIndexOf(scope);
            if (index >= 0) _stack.RemoveAt(index);
        }
    }
}
