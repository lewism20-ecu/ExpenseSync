using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using ExpenseSync.App.Services;
using ExpenseSync.App.Views;
using Microsoft.Extensions.Logging;
using SQLite;

namespace ExpenseSync.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ExpenseDatabase database;
    private readonly ILogger<MainViewModel> logger;

    private readonly SyncService syncService;

    public MainViewModel(ExpenseDatabase database, SyncService syncService, ILogger<MainViewModel> logger)
    {
        this.database = database;
        this.syncService = syncService;
        this.logger = logger;
        DatabasePath = database.DatabasePath;
    }

    public ObservableCollection<Expense> Expenses { get; } = [];

    [ObservableProperty]
    public partial string DatabaseStatus { get; set; } = "Not opened";

    [ObservableProperty]
    public partial string DatabasePath { get; set; }

    [ObservableProperty]
    public partial int PendingChangeCount { get; set; }

    [ObservableProperty]
    public partial string SyncStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [RelayCommand]
    private async Task LoadExpensesAsync()
    {
        try
        {
            var expenses = await database.GetExpensesAsync();
            Expenses.Clear();
            foreach (var expense in expenses)
            {
                Expenses.Add(expense);
            }

            PendingChangeCount = await database.GetPendingChangeCountAsync();
            DatabaseStatus = "Ready";
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Could not read the local database at {DatabasePath}", DatabasePath);
            DatabaseStatus = $"Error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNowAsync()
    {
        IsSyncing = true;
        SyncStatus = "Syncing...";
        try
        {
            var result = await syncService.PushAsync();
            SyncStatus = result.Message;
            await LoadExpensesAsync();
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private bool CanSync() => !IsSyncing;

    partial void OnIsSyncingChanged(bool value) => SyncNowCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private Task AddExpenseAsync() =>
        Shell.Current.GoToAsync(nameof(ExpenseEditPage));

    [RelayCommand]
    private Task EditExpenseAsync(Expense expense) =>
        Shell.Current.GoToAsync($"{nameof(ExpenseEditPage)}?expenseId={expense.ExpenseId}");
}
