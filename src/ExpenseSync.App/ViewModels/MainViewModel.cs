using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpenseSync.App.Data;
using Microsoft.Extensions.Logging;
using SQLite;

namespace ExpenseSync.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ExpenseDatabase database;
    private readonly ILogger<MainViewModel> logger;

    public MainViewModel(ExpenseDatabase database, ILogger<MainViewModel> logger)
    {
        this.database = database;
        this.logger = logger;
        DatabasePath = database.DatabasePath;
    }

    [ObservableProperty]
    public partial string DatabaseStatus { get; set; } = "Not opened";

    [ObservableProperty]
    public partial string DatabasePath { get; set; }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try
        {
            await database.InitializeAsync();
            DatabaseStatus = "Ready";
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Could not open the local database at {DatabasePath}", DatabasePath);
            DatabaseStatus = $"Error: {ex.Message}";
        }
    }
}
