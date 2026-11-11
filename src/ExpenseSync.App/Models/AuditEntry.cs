using SQLite;

namespace ExpenseSync.App.Models;

[Table("AuditEntries")]
public class AuditEntry
{
    [PrimaryKey]
    public Guid AuditEntryId { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid ExpenseId { get; set; }

    public ChangeOperation Operation { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    [NotNull, MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;

    [NotNull, MaxLength(100)]
    public string UserId { get; set; } = string.Empty;

    [NotNull]
    public string Details { get; set; } = string.Empty;

    public bool IsSynced { get; set; }

    public DateTime? SyncedAtUtc { get; set; }
}
