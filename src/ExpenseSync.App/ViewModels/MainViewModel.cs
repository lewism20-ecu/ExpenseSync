using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using ExpenseSync.App.Services;
using ExpenseSync.App.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Networking;
using SQLite;

namespace ExpenseSync.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ExpenseDatabase database;
    private readonly ILogger<MainViewModel> logger;
    private readonly SyncCoordinator syncCoordinator;
    private readonly SemaphoreSlim loadGate = new(1, 1);

    public MainViewModel(ExpenseDatabase database, SyncCoordinator syncCoordinator, ILogger<MainViewModel> logger)
    {
        this.database = database;
        this.syncCoordinator = syncCoordinator;
        this.logger = logger;
        DatabasePath = database.DatabasePath;
        NetworkStatus = FormatNetworkStatus(syncCoordinator.CurrentNetworkAccess);
        syncCoordinator.NetworkChanged += OnNetworkChanged;
        syncCoordinator.SyncCompleted += OnSyncCompleted;
        syncCoordinator.SyncError += OnSyncError;
    }

    public ObservableCollection<Expense> Expenses { get; } = [];

    [ObservableProperty]
    public partial string DatabaseStatus { get; set; } = "Not opened";

    [ObservableProperty]
    public partial string DatabasePath { get; set; }

    [ObservableProperty]
    public partial int PendingChangeCount { get; set; }

    [ObservableProperty]
    public partial int AuditEntryCount { get; set; }

    [ObservableProperty]
    public partial string SyncStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastSyncAttempt { get; set; } = "No sync attempts yet.";

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [RelayCommand]
    private async Task LoadExpensesAsync()
    {
        await loadGate.WaitAsync();
        try
        {
            var expenses = await database.GetExpensesAsync();
            var pendingCount = await database.GetPendingChangeCountAsync();
            var auditCount = await database.GetAuditEntryCountAsync();
            var lastAttempt = await database.GetLastSyncAttemptAsync();
            Expenses.Clear();
            foreach (var expense in expenses)
            {
                Expenses.Add(expense);
            }

            PendingChangeCount = pendingCount;
            AuditEntryCount = auditCount;
            LastSyncAttempt = lastAttempt is null ? "No sync attempts yet." : FormatAttempt(lastAttempt);
            DatabaseStatus = "Ready";
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Could not read the local database at {DatabasePath}", DatabasePath);
            DatabaseStatus = $"Error: {ex.Message}";
        }
        finally
        {
            loadGate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNowAsync()
    {
        IsSyncing = true;
        SyncStatus = "Syncing...";
        try
        {
            var result = await syncCoordinator.RunAsync(SyncTrigger.Manual);
            SyncStatus = result.Message;
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Manual sync could not access the local database.");
            SyncStatus = "Sync failed: could not access the local database.";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private bool CanSync() => !IsSyncing;

    partial void OnIsSyncingChanged(bool value) => SyncNowCommand.NotifyCanExecuteChanged();

    private void OnNetworkChanged(NetworkAccess access) =>
        MainThread.BeginInvokeOnMainThread(() => NetworkStatus = FormatNetworkStatus(access));

    private void OnSyncCompleted(SyncAttempt attempt) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            SyncStatus = attempt.Message;
            LastSyncAttempt = FormatAttempt(attempt);
            await LoadExpensesAsync();
        });

    private void OnSyncError(string message) =>
        MainThread.BeginInvokeOnMainThread(() => SyncStatus = message);

    private static string FormatNetworkStatus(NetworkAccess access) =>
        access == NetworkAccess.Internet ? "Network: Online" : "Network: Offline";

    private static string FormatAttempt(SyncAttempt attempt)
    {
        var localTime = DateTime.SpecifyKind(attempt.StartedAtUtc, DateTimeKind.Utc).ToLocalTime();
        return $"{attempt.Trigger} sync {localTime:t}: {attempt.Message}";
    }

    [RelayCommand]
    private Task AddExpenseAsync() =>
        Shell.Current.GoToAsync(nameof(ExpenseEditPage));

    [RelayCommand]
    private Task EditExpenseAsync(Expense expense) =>
        Shell.Current.GoToAsync($"{nameof(ExpenseEditPage)}?expenseId={expense.ExpenseId}");
}
