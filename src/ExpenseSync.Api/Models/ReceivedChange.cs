namespace ExpenseSync.Api.Models;

/// <summary>
/// A change received from a device that is waiting to be applied to the central server database.
/// </summary>
public class ReceivedChange
{
    public Guid PendingChangeId { get; set; }

    public Guid ExpenseId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public ChangeOperation Operation { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateOnly ExpenseDate { get; set; }

    public DateTime LastModifiedUtc { get; set; }

    public bool IsProcessed { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }
}
