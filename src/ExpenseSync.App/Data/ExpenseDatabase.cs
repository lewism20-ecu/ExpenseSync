using ExpenseSync.App.Models;
using SQLite;

namespace ExpenseSync.App.Data;

/// <summary>
/// Owns the device-local SQLite database. The app never touches the server database directly.
/// </summary>
public class ExpenseDatabase
{
    private readonly SemaphoreSlim initLock = new(1, 1);
    private SQLiteAsyncConnection? database;

    public string DatabasePath => DatabaseConstants.DatabasePath;

    public async Task InitializeAsync()
    {
        if (database is not null)
        {
            return;
        }

        await initLock.WaitAsync();
        try
        {
            if (database is not null)
            {
                return;
            }

            // Store DateTime columns as readable ISO text instead of ticks so the tables are easy to inspect.
            var connection = new SQLiteAsyncConnection(DatabasePath, DatabaseConstants.Flags, storeDateTimeAsTicks: false);
            await connection.CreateTableAsync<Expense>();
            await connection.CreateTableAsync<PendingChange>();
            await connection.CreateTableAsync<SyncState>();
            await connection.CreateTableAsync<IncomingChange>();
            if (await connection.FindAsync<SyncState>(SyncState.SingletonId) is null)
            {
                await connection.InsertAsync(new SyncState());
            }

            database = connection;
        }
        finally
        {
            initLock.Release();
        }
    }

    // ----- Expenses -----

    /// <summary>Returns expenses that are not deleted, newest expense date first.</summary>
    public async Task<List<Expense>> GetExpensesAsync()
    {
        await InitializeAsync();
        return await database!.Table<Expense>()
            .Where(e => !e.IsDeleted)
            .OrderByDescending(e => e.ExpenseDate)
            .ToListAsync();
    }

    public async Task<Expense?> GetExpenseAsync(Guid expenseId)
    {
        await InitializeAsync();
        return await database!.Table<Expense>()
            .Where(e => e.ExpenseId == expenseId && !e.IsDeleted)
            .FirstOrDefaultAsync();
    }

    /// <summary>Inserts a new expense and records a pending Create in the same transaction.</summary>
    public async Task CreateExpenseAsync(Expense expense)
    {
        await InitializeAsync();
        var now = DateTime.UtcNow;
        expense.LastModifiedUtc = now;

        await database!.RunInTransactionAsync(conn =>
        {
            conn.Insert(expense);
            conn.Insert(NewPendingChange(expense.ExpenseId, ChangeOperation.Create, now));
        });
    }

    /// <summary>Saves edits and records a pending Update in the same transaction.</summary>
    public async Task UpdateExpenseAsync(Expense expense)
    {
        await InitializeAsync();
        var now = DateTime.UtcNow;
        expense.LastModifiedUtc = now;

        await database!.RunInTransactionAsync(conn =>
        {
            conn.Update(expense);

            var pending = FindPendingChange(conn, expense.ExpenseId);
            if (pending is null)
            {
                conn.Insert(NewPendingChange(expense.ExpenseId, ChangeOperation.Update, now));
            }
            else
            {
                // A still-unsynced Create stays a Create; an Update stays an Update.
                // Either way the server only needs the latest version of the row.
                ReplacePendingChange(conn, pending, pending.Operation, now);
            }
        });
    }

    /// <summary>Marks the expense deleted (tombstone) and records a pending Delete in the same transaction.</summary>
    public async Task DeleteExpenseAsync(Expense expense)
    {
        await InitializeAsync();
        var now = DateTime.UtcNow;

        await database!.RunInTransactionAsync(conn =>
        {
            var pending = FindPendingChange(conn, expense.ExpenseId);

            if (pending?.Operation == ChangeOperation.Create)
            {
                // The server never saw this expense, so there is nothing to sync.
                conn.Delete(pending);
                conn.Delete(expense);
                return;
            }

            expense.IsDeleted = true;
            expense.LastModifiedUtc = now;
            conn.Update(expense);

            if (pending is null)
            {
                conn.Insert(NewPendingChange(expense.ExpenseId, ChangeOperation.Delete, now));
            }
            else
            {
                ReplacePendingChange(conn, pending, ChangeOperation.Delete, now);
            }
        });
    }

    // ----- Pending changes -----

    public async Task<List<PendingChange>> GetPendingChangesAsync()
    {
        await InitializeAsync();
        return await database!.Table<PendingChange>()
            .OrderBy(p => p.CapturedAtUtc)
            .ToListAsync();
    }

    /// <summary>Loads an expense row even if it is a tombstone.</summary>
    public async Task<Expense?> GetExpenseIncludingDeletedAsync(Guid expenseId)
    {
        await InitializeAsync();
        return await database!.Table<Expense>()
            .Where(e => e.ExpenseId == expenseId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Deletes only the given pending rows in one transaction, and hard-deletes tombstoned
    /// expenses whose Delete was accepted.
    /// </summary>
    public async Task ClearPendingChangesAsync(IEnumerable<Guid> acceptedIds)
    {
        await InitializeAsync();
        var ids = acceptedIds.ToHashSet();
        if (ids.Count == 0)
        {
            return;
        }

        await database!.RunInTransactionAsync(conn =>
        {
            foreach (var pending in conn.Table<PendingChange>().ToList().Where(p => ids.Contains(p.PendingChangeId)))
            {
                conn.Delete(pending);

                if (pending.Operation == ChangeOperation.Delete)
                {
                    var expense = conn.Find<Expense>(pending.ExpenseId);
                    if (expense is { IsDeleted: true })
                    {
                        conn.Delete(expense);
                    }
                }
            }
        });
    }

    public async Task<int> GetPendingChangeCountAsync()
    {
        await InitializeAsync();
        return await database!.Table<PendingChange>().CountAsync();
    }

    public async Task<DateTime?> GetLastSyncUtcAsync()
    {
        await InitializeAsync();
        var state = await database!.Table<SyncState>()
            .Where(s => s.Id == SyncState.SingletonId)
            .FirstOrDefaultAsync();
        return state?.LastSyncUtc is { } lastSyncUtc
            ? DateTime.SpecifyKind(lastSyncUtc, DateTimeKind.Utc)
            : null;
    }

    /// <summary>
    /// Replaces any unprocessed batch with the latest pull response. This lets a failed apply retry
    /// from the unchanged sync position without accumulating duplicate incoming rows.
    /// </summary>
    public async Task SaveIncomingChangesAsync(IEnumerable<IncomingChange> incomingChanges)
    {
        await InitializeAsync();
        var changes = incomingChanges.ToList();

        await database!.RunInTransactionAsync(conn =>
        {
            foreach (var unprocessed in conn.Table<IncomingChange>().Where(c => !c.IsProcessed).ToList())
            {
                conn.Delete(unprocessed);
            }

            foreach (var change in changes)
            {
                conn.Insert(change);
            }
        });
    }

    /// <summary>
    /// Applies the current incoming batch and advances the sync position in one transaction.
    /// </summary>
    public async Task ApplyIncomingChangesAsync(DateTime serverTimeUtc)
    {
        await InitializeAsync();
        var utcServerTime = DateTime.SpecifyKind(serverTimeUtc, DateTimeKind.Utc);

        await database!.RunInTransactionAsync(conn =>
        {
            var incomingChanges = conn.Table<IncomingChange>()
                .Where(c => !c.IsProcessed)
                .OrderBy(c => c.ServerUpdatedUtc)
                .ToList();

            foreach (var incoming in incomingChanges)
            {
                var pending = conn.Table<PendingChange>()
                    .FirstOrDefault(p => p.ExpenseId == incoming.ExpenseId);
                var expense = conn.Find<Expense>(incoming.ExpenseId);

                if (pending is not null)
                {
                    incoming.Result = IncomingChangeResult.Skipped;
                }
                else if (incoming.IsDeleted)
                {
                    if (expense is not null)
                    {
                        expense.IsDeleted = true;
                        expense.LastModifiedUtc = DateTime.SpecifyKind(incoming.LastModifiedUtc, DateTimeKind.Utc);
                        conn.Update(expense);
                    }

                    incoming.Result = IncomingChangeResult.Deleted;
                }
                else
                {
                    var receivedExpense = new Expense
                    {
                        ExpenseId = incoming.ExpenseId,
                        Description = incoming.Description,
                        Amount = incoming.Amount,
                        ExpenseDate = incoming.ExpenseDate.Date,
                        LastModifiedUtc = DateTime.SpecifyKind(incoming.LastModifiedUtc, DateTimeKind.Utc),
                        IsDeleted = false
                    };

                    if (expense is null)
                    {
                        conn.Insert(receivedExpense);
                        incoming.Result = IncomingChangeResult.Inserted;
                    }
                    else
                    {
                        receivedExpense.IsDeleted = false;
                        conn.Update(receivedExpense);
                        incoming.Result = IncomingChangeResult.Updated;
                    }
                }

                incoming.IsProcessed = true;
                incoming.ProcessedAtUtc = DateTime.UtcNow;
                conn.Update(incoming);
            }

            var state = conn.Find<SyncState>(SyncState.SingletonId) ?? new SyncState();
            state.LastSyncUtc = utcServerTime;
            conn.InsertOrReplace(state);
        });
    }

    private static PendingChange? FindPendingChange(SQLiteConnection conn, Guid expenseId) =>
        conn.Table<PendingChange>().FirstOrDefault(p => p.ExpenseId == expenseId);

    // The merged row gets a new PendingChangeId. The server ignores IDs it has already stored,
    // so reusing the old ID would drop this newer version if an earlier push reached the server
    // but its response was lost.
    private static void ReplacePendingChange(SQLiteConnection conn, PendingChange pending, ChangeOperation operation, DateTime capturedAtUtc)
    {
        conn.Delete(pending);
        conn.Insert(NewPendingChange(pending.ExpenseId, operation, capturedAtUtc));
    }

    private static PendingChange NewPendingChange(Guid expenseId, ChangeOperation operation, DateTime capturedAtUtc) =>
        new()
        {
            ExpenseId = expenseId,
            Operation = operation,
            CapturedAtUtc = capturedAtUtc,
            DeviceId = DeviceSettings.DeviceId
        };
}
