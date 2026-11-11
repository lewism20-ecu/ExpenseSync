namespace ExpenseSync.Api.Models;

/// <summary>
/// An append-only audit event received from a device.
/// </summary>
public class AuditEntry
{
    public Guid AuditEntryId { get; set; }

    public Guid ExpenseId { get; set; }

    public ChangeOperation Operation { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public DateTime ReceivedAtUtc { get; set; }
}
