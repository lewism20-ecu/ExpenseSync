using System.Globalization;
using System.Text.Json.Serialization;
using ExpenseSync.Api.Data;
using ExpenseSync.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExpenseSync.Api.Endpoints;

public static class SyncEndpoints
{
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/changes", PullAsync)
            .WithName("PullChanges")
            .WithSummary("Get server-side expense changes since a timestamp")
            .WithDescription("Returns all expense changes that were written after the requested UTC time, including tombstones.")
            .Produces<PullResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        routes.MapGet("/api/sync/changes", PullAsync)
            .WithName("PullChangesAlias")
            .ExcludeFromDescription();

        routes.MapPost("/pushChanges", PushAsync)
            .WithName("PushChanges")
            .WithSummary("Accept pending device changes")
            .WithDescription("Stores a device's pending changes and audit entries, then applies pending changes.")
            .Accepts<PushRequest>("application/json")
            .Produces<PushResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        routes.MapPost("/api/sync/push", PushAsync)
            .WithName("PushChangesAlias")
            .ExcludeFromDescription();

        routes.MapPost("/api/sync/process", ProcessAsync)
            .WithName("ProcessPendingChanges")
            .WithSummary("Apply queued device changes")
            .WithDescription("Applies unprocessed received changes in captured time order using last-captured-on-device-wins.")
            .Produces<ProcessResponse>(StatusCodes.Status200OK);

        return routes;
    }

    private static Task<IResult> PullAsync(ExpenseSyncDbContext db, [AsParameters] PullRequest request, CancellationToken cancellationToken) =>
        RunSerializedAsync(() => PullCoreAsync(db, request, cancellationToken), cancellationToken);

    private static async Task<IResult> PullCoreAsync(ExpenseSyncDbContext db, PullRequest request, CancellationToken cancellationToken)
    {
        DateTime? sinceUtc = null;
        if (request.Since is { } since)
        {
            if (!DateTimeOffset.TryParseExact(
                    since,
                    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedSince))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["since"] = ["The since value must be an ISO 8601 UTC timestamp."]
                });
            }

            sinceUtc = parsedSince.UtcDateTime;
        }

        var serverTimeUtc = DateTime.UtcNow;

        var changes = await db.Expenses
            .AsNoTracking()
            .Where(e => sinceUtc == null || e.ServerUpdatedUtc > sinceUtc.Value)
            .OrderBy(e => e.ServerUpdatedUtc)
            .Select(e => new PullExpense(
                e.ExpenseId,
                e.Description,
                e.Amount,
                e.ExpenseDate,
                e.LastModifiedUtc,
                e.IsDeleted,
                e.LastDeviceId,
                e.ServerUpdatedUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(new PullResponse(changes, serverTimeUtc));
    }

    private static Task<IResult> PushAsync(ExpenseSyncDbContext db, PushRequest request, CancellationToken cancellationToken) =>
        RunSerializedAsync(() => PushCoreAsync(db, request, cancellationToken), cancellationToken);

    private static async Task<IResult> PushCoreAsync(ExpenseSyncDbContext db, PushRequest request, CancellationToken cancellationToken)
    {
        if (request.DeviceId is not { Length: > 0 })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(PushRequest.DeviceId)] = ["A device ID is required."]
            });
        }

        var changes = request.Changes ?? [];
        var auditEntries = request.AuditEntries ?? [];
        if (changes.Count == 0 && auditEntries.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(PushRequest.Changes)] = ["At least one change or audit entry is required."]
            });
        }

        var dedupedChanges = new List<PushChange>();
        var seenPendingIds = new HashSet<Guid>();

        foreach (var change in changes)
        {
            if (change.PendingChangeId == Guid.Empty)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PushChange.PendingChangeId)] = ["Each change must include a valid pending change ID."]
                });
            }

            if (!Enum.IsDefined(change.Operation))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PushChange.Operation)] = [$"Change {change.PendingChangeId} has an invalid operation."]
                });
            }

            if (change.Operation != ChangeOperation.Delete && change.Expense is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PushChange.Expense)] = [$"Change {change.PendingChangeId} must include the expense payload for creates and updates."]
                });
            }

            if (!seenPendingIds.Add(change.PendingChangeId))
            {
                continue;
            }

            dedupedChanges.Add(change);
        }

        var dedupedAuditEntries = new List<PushAuditEntry>();
        var seenAuditEntryIds = new HashSet<Guid>();
        foreach (var auditEntry in auditEntries)
        {
            if (auditEntry.AuditEntryId == Guid.Empty)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PushAuditEntry.AuditEntryId)] = ["Each audit entry must include a valid audit entry ID."]
                });
            }

            if (!Enum.IsDefined(auditEntry.Operation))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PushAuditEntry.Operation)] = [$"Audit entry {auditEntry.AuditEntryId} has an invalid operation."]
                });
            }

            if (seenAuditEntryIds.Add(auditEntry.AuditEntryId))
            {
                dedupedAuditEntries.Add(auditEntry);
            }
        }

        var acceptedPendingIds = new List<Guid>();
        var pendingIdSet = dedupedChanges.Select(c => c.PendingChangeId).ToHashSet();
        var existingPendingIds = pendingIdSet.Count == 0
            ? []
            : await db.ReceivedChanges
                .AsNoTracking()
                .Where(c => pendingIdSet.Contains(c.PendingChangeId))
                .Select(c => c.PendingChangeId)
                .ToListAsync(cancellationToken);
        var existingPendingIdSet = existingPendingIds.ToHashSet();

        var hasNewRows = false;
        foreach (var change in dedupedChanges)
        {
            // Already stored by an earlier attempt (e.g. the response was lost): acknowledge it
            // again so the device can clear it, but do not insert a duplicate row.
            if (existingPendingIdSet.Contains(change.PendingChangeId))
            {
                acceptedPendingIds.Add(change.PendingChangeId);
                continue;
            }

            var payload = change.Expense;
            var record = new ReceivedChange
            {
                PendingChangeId = change.PendingChangeId,
                ExpenseId = change.ExpenseId,
                DeviceId = request.DeviceId,
                Operation = change.Operation,
                CapturedAtUtc = change.CapturedAtUtc,
                Description = payload?.Description ?? string.Empty,
                Amount = payload?.Amount ?? 0m,
                ExpenseDate = payload?.ExpenseDate ?? default,
                LastModifiedUtc = payload?.LastModifiedUtc ?? change.CapturedAtUtc,
                IsProcessed = false,
                ProcessedAtUtc = null
            };

            db.ReceivedChanges.Add(record);
            acceptedPendingIds.Add(change.PendingChangeId);
            hasNewRows = true;
        }

        var acceptedAuditEntryIds = new List<Guid>();
        var auditIdSet = dedupedAuditEntries.Select(a => a.AuditEntryId).ToHashSet();
        var existingAuditIds = auditIdSet.Count == 0
            ? []
            : await db.AuditEntries
                .AsNoTracking()
                .Where(a => auditIdSet.Contains(a.AuditEntryId))
                .Select(a => a.AuditEntryId)
                .ToListAsync(cancellationToken);
        var existingAuditIdSet = existingAuditIds.ToHashSet();

        foreach (var auditEntry in dedupedAuditEntries)
        {
            acceptedAuditEntryIds.Add(auditEntry.AuditEntryId);
            if (existingAuditIdSet.Contains(auditEntry.AuditEntryId))
            {
                continue;
            }

            db.AuditEntries.Add(new AuditEntry
            {
                AuditEntryId = auditEntry.AuditEntryId,
                ExpenseId = auditEntry.ExpenseId,
                Operation = auditEntry.Operation,
                OccurredAtUtc = auditEntry.OccurredAtUtc,
                DeviceId = auditEntry.DeviceId,
                UserId = auditEntry.UserId,
                Details = auditEntry.Details,
                ReceivedAtUtc = DateTime.UtcNow
            });
            hasNewRows = true;
        }

        if (hasNewRows)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await ProcessPendingChangesAsync(db, cancellationToken);

        var storedResults = acceptedPendingIds.Count == 0
            ? []
            : await db.ReceivedChanges
                .AsNoTracking()
                .Where(c => pendingIdSet.Contains(c.PendingChangeId))
                .Select(c => new
                {
                    c.PendingChangeId,
                    c.ExpenseId,
                    c.Result
                })
                .ToListAsync(cancellationToken);
        var resultByPendingId = storedResults.ToDictionary(c => c.PendingChangeId);
        var results = dedupedChanges
            .Where(change => resultByPendingId.ContainsKey(change.PendingChangeId))
            .Select(change => resultByPendingId[change.PendingChangeId])
            .Where(change => change.Result is not null)
            .Select(change => new ChangeResult(change.PendingChangeId, change.ExpenseId, change.Result!))
            .ToList();

        return Results.Ok(new PushResponse(
            acceptedPendingIds,
            acceptedAuditEntryIds,
            results,
            DateTimeOffset.UtcNow));
    }

    private static Task<IResult> ProcessAsync(ExpenseSyncDbContext db, CancellationToken cancellationToken) =>
        RunSerializedAsync(() => ProcessCoreAsync(db, cancellationToken), cancellationToken);

    private static async Task<IResult> ProcessCoreAsync(ExpenseSyncDbContext db, CancellationToken cancellationToken)
    {
        var processedChanges = await ProcessPendingChangesAsync(db, cancellationToken);

        return Results.Ok(new ProcessResponse(processedChanges.Count, DateTimeOffset.UtcNow));
    }

    private static async Task<List<ProcessedChange>> ProcessPendingChangesAsync(
        ExpenseSyncDbContext db,
        CancellationToken cancellationToken)
    {
        var queuedChanges = await db.ReceivedChanges
            .Where(c => !c.IsProcessed)
            .OrderBy(c => c.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        var processedChanges = new List<ProcessedChange>(queuedChanges.Count);
        foreach (var change in queuedChanges)
        {
            var expense = db.Expenses.Local.FirstOrDefault(e => e.ExpenseId == change.ExpenseId)
                ?? await db.Expenses.FirstOrDefaultAsync(e => e.ExpenseId == change.ExpenseId, cancellationToken);
            var incomingLastModifiedUtc = change.Operation == ChangeOperation.Delete
                ? change.CapturedAtUtc
                : change.LastModifiedUtc;
            var serverLastModifiedUtc = expense?.LastModifiedUtc;
            var nowUtc = DateTime.UtcNow;
            var result = "Applied";

            if (expense is null)
            {
                // A delete can arrive before an older, delayed create.
                db.Expenses.Add(new Expense
                {
                    ExpenseId = change.ExpenseId,
                    Description = change.Description,
                    Amount = change.Amount,
                    ExpenseDate = change.ExpenseDate,
                    LastModifiedUtc = incomingLastModifiedUtc,
                    IsDeleted = change.Operation == ChangeOperation.Delete,
                    LastDeviceId = change.DeviceId,
                    ServerUpdatedUtc = nowUtc
                });
            }
            else if (incomingLastModifiedUtc <= expense.LastModifiedUtc)
            {
                expense.ServerUpdatedUtc = nowUtc;
                result = "RejectedOlder";
            }
            else if (change.Operation == ChangeOperation.Delete)
            {
                expense.IsDeleted = true;
                expense.LastModifiedUtc = incomingLastModifiedUtc;
                expense.LastDeviceId = change.DeviceId;
                expense.ServerUpdatedUtc = nowUtc;
            }
            else
            {
                expense.Description = change.Description;
                expense.Amount = change.Amount;
                expense.ExpenseDate = change.ExpenseDate;
                expense.LastModifiedUtc = incomingLastModifiedUtc;
                expense.IsDeleted = false;
                expense.LastDeviceId = change.DeviceId;
                expense.ServerUpdatedUtc = nowUtc;
            }

            change.Result = result;
            change.ServerLastModifiedUtc = serverLastModifiedUtc;
            change.IsProcessed = true;
            change.ProcessedAtUtc = nowUtc;
            processedChanges.Add(new ProcessedChange(change.PendingChangeId, change.ExpenseId, result));
        }

        await db.SaveChangesAsync(cancellationToken);

        return processedChanges;
    }

    // Keep conflict decisions and pull cursors consistent while multiple devices sync.
    private static async Task<IResult> RunSerializedAsync(Func<Task<IResult>> operation, CancellationToken cancellationToken)
    {
        await SyncGate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            SyncGate.Release();
        }
    }

    public sealed record PullRequest(
        [property: FromQuery(Name = "since")] string? Since);

    public sealed record PullResponse(
        List<PullExpense> Changes,
        DateTime ServerTimeUtc);

    public sealed record PullExpense(
        Guid ExpenseId,
        string Description,
        decimal Amount,
        DateOnly ExpenseDate,
        DateTime LastModifiedUtc,
        bool IsDeleted,
        string LastDeviceId,
        DateTime ServerUpdatedUtc);

    public sealed record PushRequest(
        string DeviceId,
        List<PushChange>? Changes,
        List<PushAuditEntry>? AuditEntries = null);

    public sealed record PushChange(
        Guid PendingChangeId,
        Guid ExpenseId,
        ChangeOperation Operation,
        DateTime CapturedAtUtc,
        PushExpense? Expense);

    public sealed record PushExpense(
        string Description,
        decimal Amount,
        DateOnly ExpenseDate,
        DateTime LastModifiedUtc);

    public sealed record PushAuditEntry(
        Guid AuditEntryId,
        Guid ExpenseId,
        ChangeOperation Operation,
        DateTime OccurredAtUtc,
        string DeviceId,
        string UserId,
        string Details);

    public sealed record PushResponse(
        List<Guid> AcceptedPendingChangeIds,
        List<Guid> AcceptedAuditEntryIds,
        List<ChangeResult> Results,
        DateTimeOffset ServerTimeUtc);

    public sealed record ChangeResult(
        Guid PendingChangeId,
        Guid ExpenseId,
        string Result);

    private sealed record ProcessedChange(
        Guid PendingChangeId,
        Guid ExpenseId,
        string Result);

    public sealed record ProcessResponse(
        int ProcessedCount,
        DateTimeOffset ServerTimeUtc);
}
