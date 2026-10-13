namespace ExpenseSync.Api.Models;

/// <summary>
/// Server-side copy of an expense stored in the central SQLite database.
/// </summary>
public class Expense
{
    // Generated on the device so expenses can be created while offline.
    public Guid ExpenseId { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateOnly ExpenseDate { get; set; }
}
