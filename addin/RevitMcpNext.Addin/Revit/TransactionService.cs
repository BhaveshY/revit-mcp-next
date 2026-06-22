using System;
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
                transaction.Start();
                try
                {
                    T result = action();
                    transaction.Commit();
                    return result;
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        transaction.RollBack();
                    }
                    throw;
                }
            }
        }
    }
}
