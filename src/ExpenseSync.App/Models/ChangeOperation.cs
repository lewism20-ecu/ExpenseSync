using SQLite;

namespace ExpenseSync.App.Models;

// Stored as text so the PendingChanges table is readable when inspected with SQLite tools.
[StoreAsText]
public enum ChangeOperation
{
    Create,
    Update,
    Delete
}
