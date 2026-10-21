using SQLite;

namespace ExpenseSync.App.Models;

/// <summary>
/// A local change that has not been sent to the server yet. One row per changed expense.
/// </summary>
[Table("PendingChanges")]
public class PendingChange
{
    [PrimaryKey]
    public Guid PendingChangeId { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid ExpenseId { get; set; }

    public ChangeOperation Operation { get; set; }

    // UTC time the user made the change on this device.
    public DateTime CapturedAtUtc { get; set; }

    [NotNull, MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;
}
