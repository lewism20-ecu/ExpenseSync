using SQLite;

namespace ExpenseSync.App.Models;

[StoreAsText]
public enum SyncTrigger
{
    Manual,
    Auto
}

[Table("SyncAttempts")]
public class SyncAttempt
{
    [PrimaryKey]
    public Guid SyncAttemptId { get; set; } = Guid.NewGuid();

    public SyncTrigger Trigger { get; set; }

    public DateTime StartedAtUtc { get; set; }

    [NotNull]
    public string NetworkAccess { get; set; } = string.Empty;

    public bool Success { get; set; }

    public int SentCount { get; set; }

    public int ReceivedCount { get; set; }

    public int PendingAfter { get; set; }

    [NotNull]
    public string Message { get; set; } = string.Empty;
}
