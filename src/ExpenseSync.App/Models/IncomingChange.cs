using SQLite;

namespace ExpenseSync.App.Models;

[Table("IncomingChanges")]
public class IncomingChange
{
    [PrimaryKey]
    public Guid IncomingChangeId { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid ExpenseId { get; set; }

    [NotNull, MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateTime ExpenseDate { get; set; }

    public DateTime LastModifiedUtc { get; set; }

    public bool IsDeleted { get; set; }

    [NotNull, MaxLength(100)]
    public string LastDeviceId { get; set; } = string.Empty;

    public DateTime ServerUpdatedUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public bool IsProcessed { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    public string? Result { get; set; }
}

public static class IncomingChangeResult
{
    public const string Inserted = "Inserted";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
    public const string Skipped = "Skipped";
}
