using ExpenseSync.Api.Data;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using ExpenseSync.App.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SQLite;
using Xunit;
using ServerOperation = ExpenseSync.Api.Models.ChangeOperation;
using static ExpenseSync.Api.Endpoints.SyncEndpoints;

namespace ExpenseSync.Tests;

public class AppSyncTests
{
    [Fact]
    public async Task Push_AuditOnlyAfterUnsentCreateAndDelete_SendsHistoryOnce()
    {
        await using var api = await TestApi.StartAsync();
        await using var device = new TestDevice();
        using var client = new HttpClient { BaseAddress = api.BaseAddress };
        var sync = new SyncService(client, device.Database);
        var expense = NewExpense(18m);
        await device.Database.CreateExpenseAsync(expense);
        expense.Amount = 20m;
        await device.Database.UpdateExpenseAsync(expense);
        await device.Database.DeleteExpenseAsync(expense);

        Assert.Empty(await device.Database.GetPendingChangesAsync());
        var result = await sync.PushAsync();
        var retry = await sync.PushAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(retry.Success, retry.Message);
        Assert.Empty(await device.Database.GetUnsyncedAuditEntriesAsync());
        Assert.Equal(3, await device.Database.GetAuditEntryCountAsync());
        await using var scope = api.App.Services.CreateAsyncScope();
        var server = scope.ServiceProvider.GetRequiredService<ExpenseSyncDbContext>();
        Assert.Equal(3, await server.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Sync_RejectedOlderEdit_BothDevicesConvergeAndAuditReachesServer()
    {
        await using var api = await TestApi.StartAsync();
        await using var firstDevice = new TestDevice();
        await using var secondDevice = new TestDevice();
        using var firstClient = new HttpClient { BaseAddress = api.BaseAddress };
        using var secondClient = new HttpClient { BaseAddress = api.BaseAddress };
        var firstSync = new SyncService(firstClient, firstDevice.Database);
        var secondSync = new SyncService(secondClient, secondDevice.Database);
        var expense = NewExpense(48.75m);
        await firstDevice.Database.CreateExpenseAsync(expense);
        var initial = await firstSync.SyncAsync();
        Assert.True(initial.Success, initial.Message);

        expense.Amount = 60m;
        await firstDevice.Database.UpdateExpenseAsync(expense);
        var local = (await firstDevice.Database.GetExpenseAsync(expense.ExpenseId))!;
        var newer = DateTime.SpecifyKind(local.LastModifiedUtc.AddMinutes(1), DateTimeKind.Utc);
        var audit = new PushAuditEntry(Guid.NewGuid(), expense.ExpenseId, ServerOperation.Update,
            newer, "device-02", "user-02", "Amount 48.75 -> 55.00");
        await api.PushAsync(new PushRequest("device-02",
        [
            new PushChange(Guid.NewGuid(), expense.ExpenseId, ServerOperation.Update, newer,
                new PushExpense("Client lunch", 55m, new DateOnly(2026, 11, 10), newer))
        ], [audit]));

        var losingSync = await firstSync.SyncAsync();
        var receivingSync = await secondSync.SyncAsync();

        Assert.True(losingSync.Success, losingSync.Message);
        Assert.Equal(1, losingSync.RejectedCount);
        Assert.True(receivingSync.Success, receivingSync.Message);
        Assert.Equal(55m, (await firstDevice.Database.GetExpenseAsync(expense.ExpenseId))!.Amount);
        Assert.Equal(55m, (await secondDevice.Database.GetExpenseAsync(expense.ExpenseId))!.Amount);
        Assert.Empty(await firstDevice.Database.GetPendingChangesAsync());
        Assert.Empty(await secondDevice.Database.GetPendingChangesAsync());
        Assert.Empty(await firstDevice.Database.GetUnsyncedAuditEntriesAsync());
        Assert.Equal(0, await secondDevice.Database.GetAuditEntryCountAsync());
        await using var scope = api.App.Services.CreateAsyncScope();
        var server = scope.ServiceProvider.GetRequiredService<ExpenseSyncDbContext>();
        Assert.Equal(3, await server.AuditEntries.CountAsync());
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task ApplyIncoming_WithPendingEdit_UsesCapturedTimeAndServerTieBreak(
        int remoteOffsetSeconds, bool serverWins)
    {
        await using var device = new TestDevice();
        var expense = NewExpense(60m);
        await device.Database.CreateExpenseAsync(expense);
        var local = (await device.Database.GetExpenseAsync(expense.ExpenseId))!;
        await device.Database.SaveIncomingChangesAsync(
        [
            new IncomingChange
            {
                ExpenseId = expense.ExpenseId,
                Description = "Client lunch",
                Amount = 55m,
                ExpenseDate = expense.ExpenseDate,
                LastModifiedUtc = local.LastModifiedUtc.AddSeconds(remoteOffsetSeconds),
                ServerUpdatedUtc = DateTime.UtcNow,
                ReceivedAtUtc = DateTime.UtcNow,
                LastDeviceId = "device-02"
            }
        ]);

        await device.Database.ApplyIncomingChangesAsync(DateTime.UtcNow);

        var after = (await device.Database.GetExpenseAsync(expense.ExpenseId))!;
        Assert.Equal(serverWins ? 55m : 60m, after.Amount);
        Assert.Equal(serverWins ? 0 : 1, await device.Database.GetPendingChangeCountAsync());
        Assert.Equal(1, await device.Database.GetAuditEntryCountAsync());
        var incoming = Assert.Single(await device.Connection.Table<IncomingChange>().ToListAsync());
        Assert.Equal(serverWins ? IncomingChangeResult.ConflictServerWins : IncomingChangeResult.ConflictLocalWins,
            incoming.Result);
    }

    [Fact]
    public async Task Push_WhenCreateIsDeletedInFlight_QueuesDeleteAndRemovesServerCopy()
    {
        await using var api = await TestApi.StartAsync();
        await using var device = new TestDevice();
        using var handler = new PauseFirstPushHandler();
        using var client = new HttpClient(handler) { BaseAddress = api.BaseAddress };
        var sync = new SyncService(client, device.Database);
        var expense = NewExpense(18m);
        await device.Database.CreateExpenseAsync(expense);

        var firstPush = sync.PushAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await device.Database.DeleteExpenseAsync(expense);
        handler.Release.TrySetResult();
        var firstResult = await firstPush;

        Assert.True(firstResult.Success, firstResult.Message);
        var pending = Assert.Single(await device.Database.GetPendingChangesAsync());
        Assert.Equal(ChangeOperation.Delete, pending.Operation);
        var secondResult = await sync.SyncAsync();
        Assert.True(secondResult.Success, secondResult.Message);
        Assert.Empty(await device.Database.GetPendingChangesAsync());
        Assert.True(Assert.Single((await api.PullAsync()).Changes).IsDeleted);
    }

    [Fact]
    public async Task Initialize_LegacyPendingCreate_TreatsItAsPossiblySent()
    {
        await using var device = new TestDevice();
        using (var connection = new SQLiteConnection(
                   device.Database.DatabasePath, DatabaseConstants.Flags, storeDateTimeAsTicks: false))
        {
            connection.CreateTable<LegacyPendingChange>();
            connection.Insert(new LegacyPendingChange
            {
                PendingChangeId = Guid.NewGuid(),
                ExpenseId = Guid.NewGuid(),
                Operation = ChangeOperation.Create,
                CapturedAtUtc = DateTime.UtcNow,
                DeviceId = "device-01"
            });
        }

        var pending = Assert.Single(await device.Database.GetPendingChangesAsync());

        Assert.True(pending.PushAttempted);
    }

    private static Expense NewExpense(decimal amount) => new()
    {
        Description = "Client lunch",
        Amount = amount,
        ExpenseDate = new DateTime(2026, 11, 10)
    };
}

[Table("PendingChanges")]
internal sealed class LegacyPendingChange
{
    [SQLite.PrimaryKey]
    public Guid PendingChangeId { get; set; }
    public Guid ExpenseId { get; set; }
    public ChangeOperation Operation { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string DeviceId { get; set; } = string.Empty;
}

internal sealed class TestDevice : IAsyncDisposable
{
    private readonly string directory;

    static TestDevice() => SQLitePCL.Batteries_V2.Init();

    public TestDevice()
    {
        directory = Path.Combine(Path.GetTempPath(), "ExpenseSync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device.db3");
        Database = new ExpenseDatabase(path);
        Connection = new SQLiteAsyncConnection(path, DatabaseConstants.Flags, storeDateTimeAsTicks: false);
    }

    public ExpenseDatabase Database { get; }

    public SQLiteAsyncConnection Connection { get; }

    public async ValueTask DisposeAsync()
    {
        await Connection.CloseAsync();
        Directory.Delete(directory, recursive: true);
    }
}

internal sealed class PauseFirstPushHandler : DelegatingHandler
{
    private int firstPush = 1;

    public PauseFirstPushHandler() : base(new HttpClientHandler())
    {
    }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && Interlocked.Exchange(ref firstPush, 0) == 1)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
