using System.Globalization;
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
            await connection.CreateTableAsync<AuditEntry>();
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
            conn.Insert(NewAuditEntry(
                expense.ExpenseId,
                ChangeOperation.Create,
                now,
                $"Created \"{expense.Description}\" {FormatAmount(expense.Amount)}"));
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
            var previous = conn.Find<Expense>(expense.ExpenseId);
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

            conn.Insert(NewAuditEntry(
                expense.ExpenseId,
                ChangeOperation.Update,
                now,
                BuildUpdateDetails(previous, expense)));
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
                conn.Insert(NewAuditEntry(
                    expense.ExpenseId,
                    ChangeOperation.Delete,
                    now,
                    $"Deleted \"{expense.Description}\""));
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

            conn.Insert(NewAuditEntry(
                expense.ExpenseId,
                ChangeOperation.Delete,
                now,
                $"Deleted \"{expense.Description}\""));
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

    public async Task<List<AuditEntry>> GetUnsyncedAuditEntriesAsync()
    {
        await InitializeAsync();
        return await database!.Table<AuditEntry>()
            .Where(a => !a.IsSynced)
            .OrderBy(a => a.OccurredAtUtc)
            .ToListAsync();
    }

    public async Task<int> GetAuditEntryCountAsync()
    {
        await InitializeAsync();
        return await database!.Table<AuditEntry>().CountAsync();
    }

    public async Task MarkAuditEntriesSyncedAsync(IEnumerable<Guid> acceptedIds)
    {
        await InitializeAsync();
        var ids = acceptedIds.ToHashSet();
        if (ids.Count == 0)
        {
            return;
        }

        var syncedAtUtc = DateTime.UtcNow;
        await database!.RunInTransactionAsync(conn =>
        {
            foreach (var entry in conn.Table<AuditEntry>()
                         .ToList()
                         .Where(a => ids.Contains(a.AuditEntryId) && !a.IsSynced))
            {
                entry.IsSynced = true;
                entry.SyncedAtUtc = syncedAtUtc;
                conn.Update(entry);
            }
        });
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
                var hasPendingChange = pending is not null;
                var incomingLastModifiedUtc = DateTime.SpecifyKind(incoming.LastModifiedUtc, DateTimeKind.Utc);
                var serverWinsConflict = hasPendingChange
                    && (expense is null || incomingLastModifiedUtc >= DateTime.SpecifyKind(expense.LastModifiedUtc, DateTimeKind.Utc));

                if (hasPendingChange && !serverWinsConflict)
                {
                    incoming.Result = IncomingChangeResult.ConflictLocalWins;
                }
                else
                {
                    if (serverWinsConflict)
                    {
                        conn.Delete(pending!);
                    }

                    if (incoming.IsDeleted)
                    {
                        if (expense is not null)
                        {
                            expense.IsDeleted = true;
                            expense.LastModifiedUtc = incomingLastModifiedUtc;
                            conn.Update(expense);
                        }

                        incoming.Result = hasPendingChange
                            ? IncomingChangeResult.ConflictServerWins
                            : IncomingChangeResult.Deleted;
                    }
                    else
                    {
                        var receivedExpense = new Expense
                        {
                            ExpenseId = incoming.ExpenseId,
                            Description = incoming.Description,
                            Amount = incoming.Amount,
                            ExpenseDate = incoming.ExpenseDate.Date,
                            LastModifiedUtc = incomingLastModifiedUtc,
                            IsDeleted = false
                        };

                        if (expense is null)
                        {
                            conn.Insert(receivedExpense);
                            incoming.Result = hasPendingChange
                                ? IncomingChangeResult.ConflictServerWins
                                : IncomingChangeResult.Inserted;
                        }
                        else
                        {
                            conn.Update(receivedExpense);
                            incoming.Result = hasPendingChange
                                ? IncomingChangeResult.ConflictServerWins
                                : IncomingChangeResult.Updated;
                        }
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

    private static AuditEntry NewAuditEntry(Guid expenseId, ChangeOperation operation, DateTime occurredAtUtc, string details) =>
        new()
        {
            ExpenseId = expenseId,
            Operation = operation,
            OccurredAtUtc = occurredAtUtc,
            DeviceId = DeviceSettings.DeviceId,
            UserId = DeviceSettings.UserId,
            Details = details,
            IsSynced = false
        };

    private static string BuildUpdateDetails(Expense? previous, Expense updated)
    {
        if (previous is null)
        {
            return $"Updated \"{updated.Description}\"";
        }

        var changes = new List<string>();
        if (previous.Amount != updated.Amount)
        {
            changes.Add($"Amount {FormatAmount(previous.Amount)} -> {FormatAmount(updated.Amount)}");
        }

        if (!string.Equals(previous.Description, updated.Description, StringComparison.Ordinal))
        {
            changes.Add($"Description \"{previous.Description}\" -> \"{updated.Description}\"");
        }

        if (previous.ExpenseDate.Date != updated.ExpenseDate.Date)
        {
            changes.Add($"Date {previous.ExpenseDate:yyyy-MM-dd} -> {updated.ExpenseDate:yyyy-MM-dd}");
        }

        return changes.Count == 0
            ? $"Updated \"{updated.Description}\""
            : string.Join("; ", changes);
    }

    private static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
