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
                pending.CapturedAtUtc = now;
                conn.Update(pending);
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
                pending.Operation = ChangeOperation.Delete;
                pending.CapturedAtUtc = now;
                conn.Update(pending);
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

    private static PendingChange? FindPendingChange(SQLiteConnection conn, Guid expenseId) =>
        conn.Table<PendingChange>().FirstOrDefault(p => p.ExpenseId == expenseId);

    private static PendingChange NewPendingChange(Guid expenseId, ChangeOperation operation, DateTime capturedAtUtc) =>
        new()
        {
            ExpenseId = expenseId,
            Operation = operation,
            CapturedAtUtc = capturedAtUtc,
            DeviceId = DeviceSettings.DeviceId
        };
}
