using ExpenseSync.Api.Data;

namespace ExpenseSync.Api.Endpoints;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/", () => Results.Ok(new
        {
            Name = "ExpenseSync API",
            Status = "Running"
        }))
        .WithName("GetApiInfo")
        .WithTags("Status");

        routes.MapGet("/health", GetHealthAsync)
            .WithName("GetHealth")
            .WithTags("Status");

        return routes;
    }

    private static async Task<IResult> GetHealthAsync(ExpenseSyncDbContext db, CancellationToken cancellationToken)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);

        return canConnect
            ? Results.Ok(new { Status = "Healthy", Database = "Connected", ServerTimeUtc = DateTimeOffset.UtcNow })
            : Results.Problem("The server database is not reachable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
