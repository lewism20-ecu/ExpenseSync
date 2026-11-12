using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Networking;
using SQLite;

namespace ExpenseSync.App.Services;

public sealed class SyncCoordinator : IDisposable
{
    private const string BusyMessage = "Sync already running.";

    private readonly ExpenseDatabase database;
    private readonly SyncService syncService;
    private readonly IConnectivity connectivity;
    private readonly ILogger<SyncCoordinator> logger;
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly object connectivityLock = new();
    private NetworkAccess lastNetworkAccess;
    private long connectivityVersion;
    private bool isListening;

    public SyncCoordinator(
        ExpenseDatabase database,
        SyncService syncService,
        IConnectivity connectivity,
        ILogger<SyncCoordinator> logger)
    {
        this.database = database;
        this.syncService = syncService;
        this.connectivity = connectivity;
        this.logger = logger;
    }

    public event Action<NetworkAccess>? NetworkChanged;

    public event Action<SyncAttempt>? SyncCompleted;

    public event Action<string>? SyncError;

    public NetworkAccess CurrentNetworkAccess => connectivity.NetworkAccess;

    public void Start()
    {
        lock (connectivityLock)
        {
            if (isListening)
            {
                return;
            }

            lastNetworkAccess = connectivity.NetworkAccess;
            connectivity.ConnectivityChanged += OnConnectivityChanged;
            isListening = true;
        }
    }

    public async Task<SyncResult> RunAsync(SyncTrigger trigger)
    {
        if (!await syncGate.WaitAsync(0))
        {
            return new SyncResult(false, 0, BusyMessage);
        }

        try
        {
            var startedAtUtc = DateTime.UtcNow;
            var networkAccess = connectivity.NetworkAccess;
            SyncResult result;
            if (networkAccess != NetworkAccess.Internet)
            {
                var pending = await database.GetPendingChangeCountAsync();
                result = new SyncResult(false, 0, $"Offline: {pending} change(s) kept for later.");
            }
            else
            {
                result = await syncService.SyncAsync();
            }

            var attempt = new SyncAttempt
            {
                Trigger = trigger,
                StartedAtUtc = startedAtUtc,
                NetworkAccess = networkAccess.ToString(),
                Success = result.Success,
                SentCount = result.SentCount,
                ReceivedCount = result.ReceivedCount,
                PendingAfter = await database.GetPendingChangeCountAsync(),
                Message = result.Message
            };
            await database.AddSyncAttemptAsync(attempt);
            logger.LogInformation(
                "{Trigger} sync: {NetworkAccess}, success {Success}, pending {PendingAfter}. {Message}",
                trigger, attempt.NetworkAccess, attempt.Success, attempt.PendingAfter, attempt.Message);
            SyncCompleted?.Invoke(attempt);
            return result;
        }
        finally
        {
            syncGate.Release();
        }
    }

    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs args)
    {
        long version;
        lock (connectivityLock)
        {
            if (args.NetworkAccess == lastNetworkAccess)
            {
                return;
            }

            lastNetworkAccess = args.NetworkAccess;
            version = ++connectivityVersion;
        }

        NetworkChanged?.Invoke(args.NetworkAccess);
        try
        {
            var result = await RunAsync(SyncTrigger.Auto);
            while (result.Message == BusyMessage)
            {
                await syncGate.WaitAsync();
                syncGate.Release();
                lock (connectivityLock)
                {
                    if (version != connectivityVersion)
                    {
                        return;
                    }
                }

                result = await RunAsync(SyncTrigger.Auto);
            }
        }
        catch (SQLiteException ex)
        {
            logger.LogError(ex, "Automatic sync could not access the local database.");
            SyncError?.Invoke("Automatic sync failed: could not access the local database.");
        }
    }

    public void Dispose()
    {
        lock (connectivityLock)
        {
            if (isListening)
            {
                connectivity.ConnectivityChanged -= OnConnectivityChanged;
                isListening = false;
            }
        }
    }
}
