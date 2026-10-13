using SQLite;

namespace ExpenseSync.App.Data;

public static class DatabaseConstants
{
    public const string DatabaseFilename = "expensesync.db3";

    public const SQLiteOpenFlags Flags =
        SQLiteOpenFlags.ReadWrite |
        SQLiteOpenFlags.Create |
        SQLiteOpenFlags.SharedCache;

    public static string DatabasePath =>
        Path.Combine(FileSystem.AppDataDirectory, DatabaseFilename);
}
