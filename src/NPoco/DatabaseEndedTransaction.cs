using System;
using System.Threading;
using System.Threading.Tasks;

namespace NPoco
{
    // SQL Server rolls a transaction back by itself after a deadlock, an error under XACT_ABORT or an error in a
    // trigger. SqlClient then clears the transaction's Connection, and a command given that transaction runs with no
    // transaction at all, so it commits on its own while the rest of the work is gone.
    public partial class Database
    {
        void ThrowIfTransactionEnded()
        {
            if (_transaction != null && _transaction.Connection == null)
            {
                throw new InvalidOperationException(
                    "Cannot run a command in the database's transaction, because it has already been committed or rolled back. " +
                    "SQL Server rolls a transaction back by itself after a deadlock, an error under XACT_ABORT or an error in a trigger, " +
                    "and a command run without it would commit on its own. If the transaction was committed elsewhere on purpose, " +
                    "call SetTransaction(null) to run commands without a transaction.");
            }
        }

        // Unlike AbortTransaction this doesn't close and reopen the connection: there is nothing left to roll back, and
        // a connection passed in by the caller must stay the caller's session.
        async Task ForgetTransactionAndThrowCannotCommit(bool sync, CancellationToken cancellationToken)
        {
            Exception? cleanupFailure = null;

            try
            {
                if (sync)
                {
                    _transaction?.Dispose();
                }
                else
                {
#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
                    if (_transaction != null) await _transaction.DisposeAsync();
#else
                    _transaction?.Dispose();
#endif
                }

                _transaction = null;
                TransactionIsAborted = false;

                if (sync)
                {
                    OnAbortTransactionInternal();
                    CloseSharedConnectionInternal();
                }
                else
                {
#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
                    await OnAbortTransactionInternalAsync(cancellationToken);
#else
                    OnAbortTransactionInternal();
#endif
                    await CloseSharedConnectionInternalAsync();
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            throw new InvalidOperationException(
                "Cannot commit the database's transaction, because it has already ended or its connection is closed. " +
                "SQL Server rolls a transaction back by itself after a deadlock, an error under XACT_ABORT or an error in a trigger, " +
                "and a transaction passed in with SetTransaction may have been committed by its owner.",
                cleanupFailure);
        }
    }
}
