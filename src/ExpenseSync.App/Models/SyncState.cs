using SQLite;

namespace ExpenseSync.App.Models;

[Table("SyncState")]
public class SyncState
{
    public const int SingletonId = 1;

    [PrimaryKey]
    public int Id { get; set; } = SingletonId;

    public DateTime? LastSyncUtc { get; set; }
}
