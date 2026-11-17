using SQLite;

namespace ExpenseSync.App.Models;

[Table("ConflictResolutions")]
public class ConflictResolution
{
    [PrimaryKey]
    public Guid PendingChangeId { get; set; }

    [Indexed]
    public Guid ExpenseId { get; set; }

    public ChangeOperation Operation { get; set; }

    [NotNull, MaxLength(50)]
    public string Outcome { get; set; } = string.Empty;

    [NotNull, MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;

    [NotNull, MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public DateTime RecordedAtUtc { get; set; }
}
