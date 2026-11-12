using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseSync.Api.Data;
using ExpenseSync.Api.Endpoints;
using ExpenseSync.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static ExpenseSync.Api.Endpoints.SyncEndpoints;

namespace ExpenseSync.Tests;

public class SyncApiTests
{
    [Fact]
    public async Task Push_MultipleChangesForNewExpense_AppliesThemInCapturedOrder()
    {
        await using var api = await TestApi.StartAsync();
        var expenseId = Guid.NewGuid();
        var first = Change(expenseId, ChangeOperation.Create, 48.75m, Utc(13));
        var second = Change(expenseId, ChangeOperation.Update, 55m, Utc(15));

        var result = await api.PushAsync(new PushRequest("device-02", [second, first]));

        Assert.All(result.Results, item => Assert.Equal("Applied", item.Result));
        Assert.Equal(2, result.AcceptedPendingChangeIds.Count);
        var expense = Assert.Single((await api.PullAsync()).Changes);
        Assert.Equal(55m, expense.Amount);
        Assert.Equal(Utc(15), expense.LastModifiedUtc);
    }

    [Fact]
    public async Task Push_OlderAndEqualChangesAndAuditRetry_PreservesWinnerAndHistory()
    {
        await using var api = await TestApi.StartAsync();
        var expenseId = Guid.NewGuid();
        var winner = Change(expenseId, ChangeOperation.Create, 55m, Utc(15));
        var older = Change(expenseId, ChangeOperation.Update, 60m, Utc(14));
        var equal = Change(expenseId, ChangeOperation.Update, 99m, Utc(15));
        var audit = new PushAuditEntry(
            Guid.NewGuid(), expenseId, ChangeOperation.Update, Utc(15),
            "device-02", "user-02", "Amount 48.75 -> 55.00");

        await api.PushAsync(new PushRequest("device-02", [winner], [audit]));
        var rejected = await api.PushAsync(new PushRequest("device-01", [older, equal]));
        var retry = await api.PushAsync(new PushRequest("device-02", [], [audit]));

        Assert.All(rejected.Results, item => Assert.Equal("RejectedOlder", item.Result));
        Assert.Contains(audit.AuditEntryId, retry.AcceptedAuditEntryIds);
        Assert.Equal(55m, Assert.Single((await api.PullAsync()).Changes).Amount);
        await using var scope = api.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpenseSyncDbContext>();
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(3, await db.ReceivedChanges.CountAsync());
    }

    [Fact]
    public async Task Push_DeleteBeforeOlderCreate_PreservesTombstone()
    {
        await using var api = await TestApi.StartAsync();
        var expenseId = Guid.NewGuid();
        var delete = new PushChange(Guid.NewGuid(), expenseId, ChangeOperation.Delete, Utc(15), null);
        var create = Change(expenseId, ChangeOperation.Create, 18m, Utc(13));

        var deleted = await api.PushAsync(new PushRequest("device-01", [delete]));
        var older = await api.PushAsync(new PushRequest("device-01", [create]));

        Assert.Equal("Applied", Assert.Single(deleted.Results).Result);
        Assert.Equal("RejectedOlder", Assert.Single(older.Results).Result);
        var expense = Assert.Single((await api.PullAsync()).Changes);
        Assert.True(expense.IsDeleted);
        Assert.Equal(Utc(15), expense.LastModifiedUtc);
    }

    [Fact]
    public async Task Push_ConcurrentChanges_PreservesMostRecentlyCapturedVersion()
    {
        await using var api = await TestApi.StartAsync();
        var expenseId = Guid.NewGuid();
        await api.PushAsync(new PushRequest(
            "device-01", [Change(expenseId, ChangeOperation.Create, 1m, Utc(13))]));
        var requests = Enumerable.Range(1, 20)
            .Select(i => new PushRequest(
                $"device-{i % 2 + 1:00}",
                [Change(expenseId, ChangeOperation.Update, i, Utc(13).AddMinutes(i))]))
            .ToArray();

        await Task.WhenAll(requests.Select(api.PushAsync));

        var expense = Assert.Single((await api.PullAsync()).Changes);
        Assert.Equal(20m, expense.Amount);
        Assert.Equal(Utc(13).AddMinutes(20), expense.LastModifiedUtc);
    }

    private static DateTime Utc(int hour) => new(2026, 11, 10, hour, 0, 0, DateTimeKind.Utc);

    private static PushChange Change(Guid expenseId, ChangeOperation operation, decimal amount, DateTime capturedAtUtc) =>
        new(Guid.NewGuid(), expenseId, operation, capturedAtUtc,
            new PushExpense("Client lunch", amount, new DateOnly(2026, 11, 10), capturedAtUtc));
}

internal sealed class TestApi : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string directory;
    private readonly HttpClient client;

    private TestApi(WebApplication app, string directory, HttpClient client)
    {
        App = app;
        this.directory = directory;
        this.client = client;
    }

    public WebApplication App { get; }

    public Uri BaseAddress => client.BaseAddress!;

    public static async Task<TestApi> StartAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExpenseSync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(SyncEndpoints).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<ExpenseSyncDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(directory, "server.db")};Pooling=False"));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.MapSyncEndpoints();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ExpenseSyncDbContext>().Database.MigrateAsync();
        }

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestApi(app, directory, new HttpClient { BaseAddress = new Uri(address) });
    }

    public async Task<PushResponse> PushAsync(PushRequest request)
    {
        using var response = await client.PostAsJsonAsync("/pushChanges", request, JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Push returned {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<PushResponse>(body, JsonOptions)!;
    }

    public async Task<PullResponse> PullAsync() =>
        (await client.GetFromJsonAsync<PullResponse>("/changes", JsonOptions))!;

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await App.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }
}
