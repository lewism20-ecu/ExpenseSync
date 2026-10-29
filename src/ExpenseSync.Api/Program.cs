using ExpenseSync.Api.Data;
using ExpenseSync.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddDbContext<ExpenseSyncDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ExpenseSyncDb")));

var app = builder.Build();

// Create or upgrade the central database from the EF Core migrations on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ExpenseSyncDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapStatusEndpoints();
app.MapSyncEndpoints();

app.Run();
