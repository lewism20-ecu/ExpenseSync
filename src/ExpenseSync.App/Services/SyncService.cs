using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using SQLite;

namespace ExpenseSync.App.Services;

public record SyncResult(bool Success, int SentCount, string Message);
public record PullResult(bool Success, int ReceivedCount, DateTime? ServerTimeUtc, string Message);

/// <summary>Pushes local changes and pulls server changes through the sync API.</summary>
public class SyncService
{
    public const string PushPath = "pushChanges";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient httpClient;
    private readonly ExpenseDatabase database;

    public SyncService(HttpClient httpClient, ExpenseDatabase database)
    {
        this.httpClient = httpClient;
        this.database = database;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var push = await PushAsync(cancellationToken);
        if (!push.Success)
        {
            return push;
        }

        var pull = await PullAsync(cancellationToken);
        if (!pull.Success || pull.ServerTimeUtc is null)
        {
            return new SyncResult(
                false,
                push.SentCount,
                $"Sent {push.SentCount} change(s), but pull failed. {pull.Message}");
        }

        try
        {
            await database.ApplyIncomingChangesAsync(pull.ServerTimeUtc.Value);
        }
        catch (SQLiteException ex)
        {
            return new SyncResult(
                false,
                push.SentCount,
                $"Sent {push.SentCount} change(s), received {pull.ReceivedCount}, but downloaded changes could not be applied: {ex.Message} The previous sync position was kept.");
        }

        return new SyncResult(
            true,
            push.SentCount,
            $"Sent {push.SentCount}, received {pull.ReceivedCount} change(s).");
    }

    public async Task<PullResult> PullAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var lastSyncUtc = await database.GetLastSyncUtcAsync();
            var requestPath = "changes";
            if (lastSyncUtc is { } since)
            {
                var utcSince = DateTime.SpecifyKind(since, DateTimeKind.Utc);
                var sinceValue = Uri.EscapeDataString(utcSince.ToString("O", CultureInfo.InvariantCulture));
                requestPath = $"{requestPath}?since={sinceValue}";
            }

            var result = await httpClient.GetFromJsonAsync<PullResponse>(requestPath, JsonOptions, cancellationToken);
            if (result?.Changes is null || result.ServerTimeUtc == default)
            {
                throw new JsonException("The server returned an incomplete pull response.");
            }

            var receivedAtUtc = DateTime.UtcNow;
            var incomingChanges = result.Changes.Select(change => new IncomingChange
            {
                ExpenseId = change.ExpenseId,
                Description = change.Description,
                Amount = change.Amount,
                ExpenseDate = change.ExpenseDate.ToDateTime(TimeOnly.MinValue),
                LastModifiedUtc = DateTime.SpecifyKind(change.LastModifiedUtc, DateTimeKind.Utc),
                IsDeleted = change.IsDeleted,
                LastDeviceId = change.LastDeviceId,
                ServerUpdatedUtc = DateTime.SpecifyKind(change.ServerUpdatedUtc, DateTimeKind.Utc),
                ReceivedAtUtc = receivedAtUtc,
                IsProcessed = false,
                ProcessedAtUtc = null,
                Result = null
            }).ToList();

            await database.SaveIncomingChangesAsync(incomingChanges);
            return new PullResult(
                true,
                incomingChanges.Count,
                DateTime.SpecifyKind(result.ServerTimeUtc, DateTimeKind.Utc),
                $"Received {incomingChanges.Count} change(s).");
        }
        catch (HttpRequestException ex)
        {
            return new PullResult(false, 0, null, $"Pull failed: {ex.Message} The previous sync position was kept.");
        }
        catch (TaskCanceledException)
        {
            return new PullResult(false, 0, null, "Pull failed: the request timed out. The previous sync position was kept.");
        }
        catch (JsonException)
        {
            return new PullResult(false, 0, null, "Pull failed: unexpected response from the server. The previous sync position was kept.");
        }
        catch (SQLiteException ex)
        {
            return new PullResult(false, 0, null, $"Pull failed: could not save downloaded changes: {ex.Message} The previous sync position was kept.");
        }
    }

    public async Task<SyncResult> PushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = await database.GetPendingChangesAsync();
            if (pending.Count == 0)
            {
                return new SyncResult(true, 0, "Nothing to sync.");
            }

            var changes = new List<PushChange>();
            foreach (var change in pending)
            {
                PushExpense? payload = null;
                if (change.Operation != ChangeOperation.Delete)
                {
                    var expense = await database.GetExpenseIncludingDeletedAsync(change.ExpenseId);
                    if (expense is null)
                    {
                        continue;
                    }

                    // sqlite-net stores decimal as REAL, so round off binary floating-point noise.
                    payload = new PushExpense(
                        expense.Description,
                        Math.Round(expense.Amount, 2),
                        DateOnly.FromDateTime(expense.ExpenseDate),
                        DateTime.SpecifyKind(expense.LastModifiedUtc, DateTimeKind.Utc));
                }

                changes.Add(new PushChange(
                    change.PendingChangeId,
                    change.ExpenseId,
                    change.Operation,
                    DateTime.SpecifyKind(change.CapturedAtUtc, DateTimeKind.Utc),
                    payload));
            }

            var request = new PushRequest(DeviceSettings.DeviceId, changes);
            using var response = await httpClient.PostAsJsonAsync(PushPath, request, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PushResponse>(JsonOptions, cancellationToken);
            var accepted = (result?.AcceptedPendingChangeIds ?? [])
                .Where(id => pending.Any(p => p.PendingChangeId == id))
                .ToList();

            await database.ClearPendingChangesAsync(accepted);

            return accepted.Count == pending.Count
                ? new SyncResult(true, accepted.Count, $"Synced {accepted.Count} change(s).")
                : new SyncResult(false, accepted.Count, $"Server accepted {accepted.Count} of {pending.Count} change(s). The rest stay pending.");
        }
        catch (HttpRequestException ex)
        {
            return new SyncResult(false, 0, $"Sync failed: {ex.Message} Pending changes were kept.");
        }
        catch (TaskCanceledException)
        {
            return new SyncResult(false, 0, "Sync failed: the request timed out. Pending changes were kept.");
        }
        catch (JsonException)
        {
            return new SyncResult(false, 0, "Sync failed: unexpected response from the server. Pending changes were kept.");
        }
    }

    private record PushRequest(string DeviceId, List<PushChange> Changes);

    private record PushChange(
        Guid PendingChangeId,
        Guid ExpenseId,
        ChangeOperation Operation,
        DateTime CapturedAtUtc,
        PushExpense? Expense);

    private record PushExpense(string Description, decimal Amount, DateOnly ExpenseDate, DateTime LastModifiedUtc);

    private record PushResponse(List<Guid> AcceptedPendingChangeIds, DateTime? ServerTimeUtc);

    private record PullResponse(List<PullExpense>? Changes, DateTime ServerTimeUtc);

    private record PullExpense(
        Guid ExpenseId,
        string Description,
        decimal Amount,
        DateOnly ExpenseDate,
        DateTime LastModifiedUtc,
        bool IsDeleted,
        string LastDeviceId,
        DateTime ServerUpdatedUtc);
}
