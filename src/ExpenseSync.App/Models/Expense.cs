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
}
