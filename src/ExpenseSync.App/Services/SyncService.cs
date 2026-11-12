using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;
using SQLite;

namespace ExpenseSync.App.Services;

public record SyncResult(bool Success, int SentCount, string Message, int RejectedCount = 0, int ReceivedCount = 0);
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

        var rejectedSummary = push.RejectedCount > 0
            ? $" ({push.RejectedCount} rejected: server version is newer or equal)"
            : string.Empty;
        var pull = await PullAsync(cancellationToken);
        if (!pull.Success || pull.ServerTimeUtc is null)
        {
            return new SyncResult(
                false,
                push.SentCount,
                $"Sent {push.SentCount}{rejectedSummary} change(s), but pull failed. {pull.Message}",
                push.RejectedCount);
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
                $"Sent {push.SentCount}{rejectedSummary} change(s), received {pull.ReceivedCount}, but downloaded changes could not be applied: {ex.Message} The previous sync position was kept.",
                push.RejectedCount,
                ReceivedCount: pull.ReceivedCount);
        }

        return new SyncResult(
            true,
            push.SentCount,
            $"Sent {push.SentCount}{rejectedSummary}, received {pull.ReceivedCount} change(s).",
            push.RejectedCount,
            pull.ReceivedCount);
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
            var snapshots = await database.GetPendingChangesForPushAsync();
            var pending = snapshots.Select(snapshot => snapshot.Change).ToList();
            var unsyncedAuditEntries = await database.GetUnsyncedAuditEntriesAsync();
            if (pending.Count == 0 && unsyncedAuditEntries.Count == 0)
            {
                return new SyncResult(true, 0, "Nothing to sync.");
            }

            var changes = new List<PushChange>();
            foreach (var snapshot in snapshots)
            {
                var change = snapshot.Change;
                PushExpense? payload = null;
                if (change.Operation != ChangeOperation.Delete)
                {
                    var expense = snapshot.Expense
                        ?? throw new InvalidDataException($"Pending change {change.PendingChangeId} has no active expense.");

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

            var auditEntries = unsyncedAuditEntries
                .Select(entry => new PushAuditEntry(
                    entry.AuditEntryId,
                    entry.ExpenseId,
                    entry.Operation,
                    DateTime.SpecifyKind(entry.OccurredAtUtc, DateTimeKind.Utc),
                    entry.DeviceId,
                    entry.UserId,
                    entry.Details))
                .ToList();

            var request = new PushRequest(DeviceSettings.DeviceId, changes, auditEntries);
            using var response = await httpClient.PostAsJsonAsync(PushPath, request, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PushResponse>(JsonOptions, cancellationToken);
            var accepted = (result?.AcceptedPendingChangeIds ?? [])
                .Where(id => pending.Any(p => p.PendingChangeId == id))
                .ToList();
            var acceptedAuditEntries = (result?.AcceptedAuditEntryIds ?? [])
                .Where(id => unsyncedAuditEntries.Any(a => a.AuditEntryId == id))
                .ToList();
            var rejectedCount = (result?.Results ?? [])
                .Count(changeResult => string.Equals(changeResult.Result, "RejectedOlder", StringComparison.Ordinal));

            await database.ClearPendingChangesAsync(accepted);
            await database.MarkAuditEntriesSyncedAsync(acceptedAuditEntries);

            return accepted.Count == pending.Count && acceptedAuditEntries.Count == unsyncedAuditEntries.Count
                ? new SyncResult(true, accepted.Count, $"Synced {accepted.Count} change(s).", rejectedCount)
                : new SyncResult(
                    false,
                    accepted.Count,
                    $"Server accepted {accepted.Count}/{pending.Count} change(s) and {acceptedAuditEntries.Count}/{unsyncedAuditEntries.Count} audit entry(ies). Unaccepted items stay queued.",
                    rejectedCount);
        }
        catch (HttpRequestException ex)
        {
            return new SyncResult(false, 0, $"Sync failed: {ex.Message} Pending changes and audit entries were kept.");
        }
        catch (TaskCanceledException)
        {
            return new SyncResult(false, 0, "Sync failed: the request timed out. Pending changes and audit entries were kept.");
        }
        catch (JsonException)
        {
            return new SyncResult(false, 0, "Sync failed: unexpected response from the server. Pending changes and audit entries were kept.");
        }
        catch (InvalidDataException ex)
        {
            return new SyncResult(false, 0, $"Sync failed: {ex.Message} Pending changes and audit entries were kept.");
        }
        catch (SQLiteException ex)
        {
            return new SyncResult(false, 0, $"Sync failed: could not access the local database: {ex.Message}");
        }
    }

    private record PushRequest(string DeviceId, List<PushChange> Changes, List<PushAuditEntry> AuditEntries);

    private record PushChange(
        Guid PendingChangeId,
        Guid ExpenseId,
        ChangeOperation Operation,
        DateTime CapturedAtUtc,
        PushExpense? Expense);

    private record PushExpense(string Description, decimal Amount, DateOnly ExpenseDate, DateTime LastModifiedUtc);

    private record PushAuditEntry(
        Guid AuditEntryId,
        Guid ExpenseId,
        ChangeOperation Operation,
        DateTime OccurredAtUtc,
        string DeviceId,
        string UserId,
        string Details);

    private sealed record PushResponse
    {
        public List<Guid> AcceptedPendingChangeIds { get; init; } = [];
        public List<Guid> AcceptedAuditEntryIds { get; init; } = [];
        public List<PushChangeResult> Results { get; init; } = [];
        public DateTime? ServerTimeUtc { get; init; }
    }

    private record PushChangeResult(Guid PendingChangeId, Guid ExpenseId, string Result);

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
