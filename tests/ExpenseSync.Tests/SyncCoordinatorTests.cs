using System.Threading.Channels;
using ExpenseSync.App.Models;
using ExpenseSync.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Networking;
using Xunit;

namespace ExpenseSync.Tests;

public class SyncCoordinatorTests
{
    [Fact]
    public async Task GetLastSyncAttempt_EqualStoredTimes_ReturnsMostRecentlyRecordedAttempt()
    {
        await using var device = new TestDevice();
        var started = new DateTime(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);
        await device.Database.AddSyncAttemptAsync(new SyncAttempt
        {
            StartedAtUtc = started,
            NetworkAccess = "None",
            Message = "First"
        });
        var second = new SyncAttempt
        {
            StartedAtUtc = started,
            NetworkAccess = "Internet",
            Message = "Second"
        };
        await device.Database.AddSyncAttemptAsync(second);

        var last = await device.Database.GetLastSyncAttemptAsync();

        Assert.Equal(second.SyncAttemptId, last?.SyncAttemptId);
    }

    [Fact]
    public async Task Connectivity_OfflineThenOnline_RecordsAttemptsAndSyncsPendingWork()
    {
        await using var api = await TestApi.StartAsync();
        await using var device = new TestDevice();
        var expense = new Expense { Description = "Parking", Amount = 20m };
        await device.Database.CreateExpenseAsync(expense);
        using var handler = new CountingHandler();
        using var client = new HttpClient(handler) { BaseAddress = api.BaseAddress };
        var connectivity = new TestConnectivity(NetworkAccess.Internet);
        using var coordinator = new SyncCoordinator(
            device.Database, new SyncService(client, device.Database), connectivity,
            NullLogger<SyncCoordinator>.Instance);
        var attempts = Channel.CreateUnbounded<SyncAttempt>();
        coordinator.SyncCompleted += attempt => attempts.Writer.TryWrite(attempt);
        coordinator.Start();

        connectivity.SetAccess(NetworkAccess.None);
        var offline = await NextAttemptAsync(attempts);
        Assert.Equal(SyncTrigger.Auto, offline.Trigger);
        Assert.Equal("None", offline.NetworkAccess);
        Assert.False(offline.Success);
        Assert.Equal(1, offline.PendingAfter);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(1, await device.Database.GetPendingChangeCountAsync());

        connectivity.SetAccess(NetworkAccess.None);
        connectivity.SetAccess(NetworkAccess.Internet);
        var online = await NextAttemptAsync(attempts);

        Assert.Equal(SyncTrigger.Auto, online.Trigger);
        Assert.True(online.Success, online.Message);
        Assert.Equal(1, online.SentCount);
        Assert.Equal(1, online.ReceivedCount);
        Assert.Equal(0, online.PendingAfter);
        Assert.Empty(await device.Database.GetPendingChangesAsync());
        Assert.Equal(2, (await device.Connection.Table<SyncAttempt>().ToListAsync()).Count);
    }

    [Fact]
    public async Task Run_OverlappingManualSync_IsSkippedAndReconnectRunsAfterward()
    {
        await using var api = await TestApi.StartAsync();
        await using var device = new TestDevice();
        await device.Database.CreateExpenseAsync(new Expense { Description = "Parking", Amount = 18m });
        using var handler = new PauseFirstPushHandler();
        using var client = new HttpClient(handler) { BaseAddress = api.BaseAddress };
        var connectivity = new TestConnectivity(NetworkAccess.Internet);
        using var coordinator = new SyncCoordinator(
            device.Database, new SyncService(client, device.Database), connectivity,
            NullLogger<SyncCoordinator>.Instance);
        var attempts = Channel.CreateUnbounded<SyncAttempt>();
        coordinator.SyncCompleted += attempt => attempts.Writer.TryWrite(attempt);
        coordinator.Start();

        var first = coordinator.RunAsync(SyncTrigger.Manual);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var skipped = await coordinator.RunAsync(SyncTrigger.Manual);
        connectivity.SetAccess(NetworkAccess.None);
        connectivity.SetAccess(NetworkAccess.Internet);
        handler.Release.TrySetResult();
        var completed = await first;
        var manualAttempt = await NextAttemptAsync(attempts);
        var autoAttempt = await NextAttemptAsync(attempts);

        Assert.False(skipped.Success);
        Assert.Equal("Sync already running.", skipped.Message);
        Assert.True(completed.Success, completed.Message);
        Assert.Equal(SyncTrigger.Manual, manualAttempt.Trigger);
        Assert.Equal(SyncTrigger.Auto, autoAttempt.Trigger);
        Assert.True(autoAttempt.Success, autoAttempt.Message);
        Assert.Equal(2, (await device.Connection.Table<SyncAttempt>().ToListAsync()).Count);
    }

    [Fact]
    public async Task Connectivity_ServerUnavailable_KeepsPendingWorkForRetry()
    {
        await using var api = await TestApi.StartAsync();
        await using var device = new TestDevice();
        await device.Database.CreateExpenseAsync(new Expense { Description = "Parking", Amount = 18m });
        using var handler = new UnavailableHandler();
        using var client = new HttpClient(handler) { BaseAddress = api.BaseAddress };
        var connectivity = new TestConnectivity(NetworkAccess.None);
        using var coordinator = new SyncCoordinator(
            device.Database, new SyncService(client, device.Database), connectivity,
            NullLogger<SyncCoordinator>.Instance);
        var attempts = Channel.CreateUnbounded<SyncAttempt>();
        coordinator.SyncCompleted += attempt => attempts.Writer.TryWrite(attempt);
        coordinator.Start();

        connectivity.SetAccess(NetworkAccess.Internet);
        var failed = await NextAttemptAsync(attempts);
        Assert.False(failed.Success);
        Assert.Equal(1, failed.PendingAfter);
        Assert.Single(await device.Database.GetUnsyncedAuditEntriesAsync());

        handler.IsUnavailable = false;
        var retry = await coordinator.RunAsync(SyncTrigger.Manual);

        Assert.True(retry.Success, retry.Message);
        Assert.Empty(await device.Database.GetPendingChangesAsync());
        Assert.Empty(await device.Database.GetUnsyncedAuditEntriesAsync());
    }

    private static async Task<SyncAttempt> NextAttemptAsync(Channel<SyncAttempt> attempts) =>
        await attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
}

internal sealed class TestConnectivity(NetworkAccess initialAccess) : IConnectivity
{
    public IEnumerable<ConnectionProfile> ConnectionProfiles => [];

    public NetworkAccess NetworkAccess { get; private set; } = initialAccess;

    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    public void SetAccess(NetworkAccess access)
    {
        NetworkAccess = access;
        ConnectivityChanged?.Invoke(this, new ConnectivityChangedEventArgs(access, []));
    }
}

internal sealed class CountingHandler() : DelegatingHandler(new HttpClientHandler())
{
    private int requestCount;

    public int RequestCount => Volatile.Read(ref requestCount);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requestCount);
        return base.SendAsync(request, cancellationToken);
    }
}

internal sealed class UnavailableHandler() : DelegatingHandler(new HttpClientHandler())
{
    public bool IsUnavailable { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        IsUnavailable
            ? throw new HttpRequestException("Test API is unavailable.")
            : base.SendAsync(request, cancellationToken);
}
