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

            var connection = new SQLiteAsyncConnection(DatabasePath, DatabaseConstants.Flags);
            await connection.CreateTableAsync<Expense>();
            database = connection;
        }
        finally
        {
            initLock.Release();
        }
    }
}
