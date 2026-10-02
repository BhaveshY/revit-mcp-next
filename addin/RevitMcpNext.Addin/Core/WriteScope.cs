using System;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The undo unit of a write (SPEC §6.1, §6.7, D3 §2.8): a TransactionGroup named per Naming (e.g.
    /// "MCP w17 create_elements.wall"), with inner Transactions that reuse the group name and FailureCapture.
    /// Assimilate() makes one undo entry; disposing without Assimilate() rolls everything back. UI thread only.
    /// </summary>
    internal sealed class WriteScope : IDisposable
    {
        private readonly TransactionGroup _group;
        private bool _finished;

        public WriteScope(Document document, string groupName)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Name = string.IsNullOrWhiteSpace(groupName) ? Naming.Write(null, "write") : groupName;
            if (document.IsModifiable)
            {
                throw new OpException(
                    ErrorCodes.RevitEditModeOrCommand,
                    "Document '" + document.Title + "' already has an open transaction (Revit is probably in a command or edit mode).");
            }
            if (document.IsReadOnly)
            {
                throw new OpException(ErrorCodes.DocReadOnly, "Document '" + document.Title + "' is read-only.");
            }

            _group = new TransactionGroup(document, Name);
            TransactionStatus status = _group.Start();
            if (status != TransactionStatus.Started)
            {
                _group.Dispose();
                throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start transaction group '" + Name + "' (" + status + ").");
            }
        }

        public Document Document { get; }

        /// <summary>The group name shown in Revit's undo list.</summary>
        public string Name { get; }

        public bool IsOpen => !_finished && _group.GetStatus() == TransactionStatus.Started;

        /// <summary>
        /// Runs <paramref name="action"/> in an inner transaction (named <paramref name="transactionName"/> or the group
        /// name) and commits it. Exceptions roll the inner transaction back and propagate; Revit-side rollbacks return
        /// Committed=false with the captured failures.
        /// </summary>
        public TxResult RunTransaction(Action action, FailurePolicy policy = null, string transactionName = null)
        {
            if (!IsOpen) throw new InvalidOperationException("The write scope '" + Name + "' is not open.");
            var capture = new FailureCapture(policy);
            var result = new TxResult();
            using (var transaction = new Transaction(Document, string.IsNullOrWhiteSpace(transactionName) ? Name : transactionName))
            {
                FailureCapture.Configure(transaction, capture);
                TransactionStatus start = transaction.Start();
                if (start != TransactionStatus.Started)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start transaction '" + Name + "' (" + start + ").");
                }
                try
                {
                    action();
                    result.Status = transaction.Commit();
                }
                catch
                {
                    TempScope.RollBackQuietly(transaction);
                    result.Warnings.AddRange(capture.Warnings);
                    result.Errors.AddRange(capture.Errors);
                    throw;
                }
            }
            result.Warnings.AddRange(capture.Warnings);
            result.Errors.AddRange(capture.Errors);
            result.Committed = result.Status == TransactionStatus.Committed;
            return result;
        }

        /// <summary>Commits the group as one undo entry.</summary>
        public TransactionStatus Assimilate()
        {
            if (_finished) throw new InvalidOperationException("The write scope '" + Name + "' is already finished.");
            _finished = true;
            return _group.Assimilate();
        }

        /// <summary>Rolls the whole group back.</summary>
        public void RollBack()
        {
            if (_finished) return;
            _finished = true;
            try
            {
                if (_group.GetStatus() == TransactionStatus.Started) _group.RollBack();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("tx", "Rolling back write scope '" + Name + "' failed.", ex);
            }
        }

        public void Dispose()
        {
            RollBack();
            _group.Dispose();
        }
    }

    /// <summary>Helpers for single transactions outside a WriteScope ("MCP ui &lt;op&gt;" view-mode changes).</summary>
    internal static class McpTransactions
    {
        /// <summary>Runs a ui-class transaction ("MCP ui isolate"): committed, no group, no blast rules.</summary>
        public static TxResult RunUi(Document document, string uiOp, Action action, FailurePolicy policy = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var capture = new FailureCapture(policy);
            var result = new TxResult();
            using (var transaction = new Transaction(document, Naming.Ui(uiOp)))
            {
                FailureCapture.Configure(transaction, capture);
                TransactionStatus start = transaction.Start();
                if (start != TransactionStatus.Started)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start transaction '" + Naming.Ui(uiOp) + "' (" + start + ").");
                }
                try
                {
                    action();
                    result.Status = transaction.Commit();
                }
                catch
                {
                    TempScope.RollBackQuietly(transaction);
                    throw;
                }
            }
            result.Warnings.AddRange(capture.Warnings);
            result.Errors.AddRange(capture.Errors);
            result.Committed = result.Status == TransactionStatus.Committed;
            return result;
        }
    }
}
