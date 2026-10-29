using System.Text.Json.Serialization;
using ExpenseSync.Api.Data;
using ExpenseSync.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ExpenseSync.Api.Endpoints;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/sync/push", PushAsync)
            .WithName("PushChanges")
            .WithSummary("Accept pending device changes")
            .WithDescription("Stores a device's pending changes for later processing on the server.")
            .Accepts<PushRequest>("application/json")
            .Produces<PushResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        routes.MapPost("/api/sync/process", ProcessAsync)
            .WithName("ProcessPendingChanges")
            .WithSummary("Apply queued device changes")
            .WithDescription("Applies unprocessed received changes to the central expense table in captured time order.")
            .Produces<ProcessResponse>(StatusCodes.Status200OK);

        return routes;
    }

    private static async Task<IResult> PushAsync(ExpenseSyncDbContext db, PushRequest request, CancellationToken cancellationToken)
    {
        if (request.DeviceId is not { Length: > 0 })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(PushRequest.DeviceId)] = ["A device ID is required."]
            });
        }

        if (request.Changes is null || request.Changes.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(PushRequest.Changes)] = ["At least one change is required."]
            });
        }

        var dedupedChanges = new List<PushChange>();
        var seenPendingIds = new HashSet<Guid>();

        foreach (var change in request.Changes)
        {
            if (change.PendingChangeId == Guid.Empty)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PushChange.PendingChangeId)] = ["Each change must include a valid pending change ID."]
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

        if (dedupedChanges.Count == 0)
        {
            return Results.Ok(new PushResponse([], DateTimeOffset.UtcNow));
        }

        var pendingIdSet = dedupedChanges.Select(c => c.PendingChangeId).ToHashSet();
        var existingIds = await db.ReceivedChanges
            .AsNoTracking()
            .Where(c => pendingIdSet.Contains(c.PendingChangeId))
            .Select(c => c.PendingChangeId)
            .ToListAsync(cancellationToken);

        var accepted = new List<Guid>();
        var hasNewRows = false;
        foreach (var change in dedupedChanges)
        {
            // Already stored by an earlier attempt (e.g. the response was lost): acknowledge it
            // again so the device can clear it, but do not insert a duplicate row.
            if (existingIds.Contains(change.PendingChangeId))
            {
                accepted.Add(change.PendingChangeId);
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
            accepted.Add(change.PendingChangeId);
            hasNewRows = true;
        }

        if (hasNewRows)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(new PushResponse(accepted, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ProcessAsync(ExpenseSyncDbContext db, CancellationToken cancellationToken)
    {
        var queuedChanges = await db.ReceivedChanges
            .Where(c => !c.IsProcessed)
            .OrderBy(c => c.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var change in queuedChanges)
        {
            var expense = await db.Expenses
                .FirstOrDefaultAsync(e => e.ExpenseId == change.ExpenseId, cancellationToken);

            switch (change.Operation)
            {
                case ChangeOperation.Create:
                    if (expense is null)
                    {
                        db.Expenses.Add(new Expense
                        {
                            ExpenseId = change.ExpenseId,
                            Description = change.Description,
                            Amount = change.Amount,
                            ExpenseDate = change.ExpenseDate,
                            LastModifiedUtc = change.LastModifiedUtc,
                            IsDeleted = false,
                            LastDeviceId = change.DeviceId
                        });
                    }
                    else
                    {
                        expense.Description = change.Description;
                        expense.Amount = change.Amount;
                        expense.ExpenseDate = change.ExpenseDate;
                        expense.LastModifiedUtc = change.LastModifiedUtc;
                        expense.IsDeleted = false;
                        expense.LastDeviceId = change.DeviceId;
                    }
                    break;

                case ChangeOperation.Update:
                    if (expense is null)
                    {
                        db.Expenses.Add(new Expense
                        {
                            ExpenseId = change.ExpenseId,
                            Description = change.Description,
                            Amount = change.Amount,
                            ExpenseDate = change.ExpenseDate,
                            LastModifiedUtc = change.LastModifiedUtc,
                            IsDeleted = false,
                            LastDeviceId = change.DeviceId
                        });
                    }
                    else
                    {
                        expense.Description = change.Description;
                        expense.Amount = change.Amount;
                        expense.ExpenseDate = change.ExpenseDate;
                        expense.LastModifiedUtc = change.LastModifiedUtc;
                        expense.IsDeleted = false;
                        expense.LastDeviceId = change.DeviceId;
                    }
                    break;

                case ChangeOperation.Delete:
                    if (expense is not null)
                    {
                        expense.IsDeleted = true;
                        expense.LastModifiedUtc = change.CapturedAtUtc;
                        expense.LastDeviceId = change.DeviceId;
                    }
                    break;
            }

            change.IsProcessed = true;
            change.ProcessedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ProcessResponse(queuedChanges.Count, DateTimeOffset.UtcNow));
    }

    public sealed record PushRequest(
        string DeviceId,
        List<PushChange> Changes);

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

    public sealed record PushResponse(
        List<Guid> AcceptedPendingChangeIds,
        DateTimeOffset ServerTimeUtc);

    public sealed record ProcessResponse(
        int ProcessedCount,
        DateTimeOffset ServerTimeUtc);
}
