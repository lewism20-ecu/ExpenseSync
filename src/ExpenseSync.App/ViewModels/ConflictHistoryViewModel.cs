using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using Microsoft.Extensions.Logging;
using SQLite;

namespace ExpenseSync.App.ViewModels;

public partial class ConflictHistoryViewModel(
    ExpenseDatabase database,
    ILogger<ConflictHistoryViewModel> logger) : ObservableObject
{
    public ObservableCollection<ConflictHistoryItem> Conflicts { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "No conflicts recorded.";

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var resolutions = await database.GetConflictResolutionsAsync();
            var incomingChanges = await database.GetConflictIncomingChangesAsync();
            var items = resolutions.Select(conflict => new ConflictHistoryItem(
                    conflict.ExpenseId,
                    conflict.Description,
                    conflict.Amount,
                    conflict.Operation.ToString(),
                    conflict.Outcome,
                    conflict.DeviceId,
                    conflict.CapturedAtUtc,
                    conflict.RecordedAtUtc))
                .Concat(incomingChanges.Select(change => new ConflictHistoryItem(
                    change.ExpenseId,
                    change.Description,
                    change.Amount,
                    change.IsDeleted ? ChangeOperation.Delete.ToString() : ChangeOperation.Update.ToString(),
                    change.Result!,
                    change.LastDeviceId,
                    change.LastModifiedUtc,
                    change.ProcessedAtUtc ?? change.ReceivedAtUtc)))
                .OrderByDescending(conflict => conflict.RecordedAtUtc)
                .ToList();

            Conflicts.Clear();
            foreach (var conflict in items)
            {
                Conflicts.Add(conflict);
            }

            Status = items.Count == 0
                ? "No conflicts recorded."
                : $"{items.Count} conflict result(s).";
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Could not load conflict history.");
            Status = $"Could not load conflict history: {ex.Message}";
        }
    }
}

public sealed record ConflictHistoryItem(
    Guid ExpenseId,
    string Description,
    decimal Amount,
    string Operation,
    string Outcome,
    string DeviceId,
    DateTime CapturedAtUtc,
    DateTime RecordedAtUtc);
