using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseSync.App.Data;
using ExpenseSync.App.Models;

namespace ExpenseSync.App.Services;

public record SyncResult(bool Success, int SentCount, string Message);

/// <summary>Pushes pending local changes to the API (POST /api/sync/push).</summary>
public class SyncService
{
    public const string PushPath = "api/sync/push";

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

                    payload = new PushExpense(
                        expense.Description,
                        expense.Amount,
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
}
