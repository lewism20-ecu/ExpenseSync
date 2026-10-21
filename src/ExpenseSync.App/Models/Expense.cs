using SQLite;

namespace ExpenseSync.App.Models;

/// <summary>
/// An expense stored in the device-local SQLite database.
/// </summary>
[Table("Expenses")]
public class Expense
{
    // Generated on the device so expenses can be created while offline.
    [PrimaryKey]
    public Guid ExpenseId { get; set; } = Guid.NewGuid();

    [NotNull, MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    // sqlite-net-pcl has no DateOnly support, so only the date part of this value is used.
    public DateTime ExpenseDate { get; set; } = DateTime.Today;

    // UTC time the device last changed this record. Later weeks use it for sync and conflicts.
    public DateTime LastModifiedUtc { get; set; }

    // Deleted expenses are kept as tombstones so the delete can be synced later.
    public bool IsDeleted { get; set; }
}
