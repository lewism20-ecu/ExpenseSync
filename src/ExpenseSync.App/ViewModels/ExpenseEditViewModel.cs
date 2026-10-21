using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using Microsoft.Extensions.Logging;
using SQLite;

namespace ExpenseSync.App.ViewModels;

[QueryProperty(nameof(ExpenseId), "expenseId")]
public partial class ExpenseEditViewModel : ObservableObject
{
    private readonly ExpenseDatabase database;
    private readonly ILogger<ExpenseEditViewModel> logger;
    private Expense? existing;

    public ExpenseEditViewModel(ExpenseDatabase database, ILogger<ExpenseEditViewModel> logger)
    {
        this.database = database;
        this.logger = logger;
    }

    // Set by Shell from the ?expenseId= query parameter. Empty means "new expense".
    [ObservableProperty]
    public partial string ExpenseId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PageTitle { get; set; } = "Add Expense";

    [ObservableProperty]
    public partial bool IsExisting { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AmountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime ExpenseDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!Guid.TryParse(ExpenseId, out var id))
        {
            return;
        }

        existing = await database.GetExpenseAsync(id);
        if (existing is null)
        {
            ErrorMessage = "That expense no longer exists.";
            return;
        }

        PageTitle = "Edit Expense";
        IsExisting = true;
        Description = existing.Description;
        AmountText = existing.Amount.ToString("0.00", CultureInfo.CurrentCulture);
        ExpenseDate = existing.ExpenseDate;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var description = Description.Trim();
        if (description.Length == 0)
        {
            ErrorMessage = "Enter a description.";
            return;
        }

        if (!decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount <= 0)
        {
            ErrorMessage = "Enter an amount greater than zero.";
            return;
        }

        ErrorMessage = string.Empty;

        try
        {
            if (existing is null)
            {
                await database.CreateExpenseAsync(new Expense
                {
                    Description = description,
                    Amount = amount,
                    ExpenseDate = ExpenseDate.Date
                });
            }
            else
            {
                existing.Description = description;
                existing.Amount = amount;
                existing.ExpenseDate = ExpenseDate.Date;
                await database.UpdateExpenseAsync(existing);
            }

            await Shell.Current.GoToAsync("..");
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Could not save expense {ExpenseId}", ExpenseId);
            ErrorMessage = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (existing is null)
        {
            return;
        }

        var confirmed = await Shell.Current.DisplayAlertAsync(
            "Delete expense?",
            $"\"{existing.Description}\" will be removed from this device.",
            "Delete",
            "Cancel");

        if (!confirmed)
        {
            return;
        }

        try
        {
            await database.DeleteExpenseAsync(existing);
            await Shell.Current.GoToAsync("..");
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Could not delete expense {ExpenseId}", ExpenseId);
            ErrorMessage = $"Could not delete: {ex.Message}";
        }
    }
}
