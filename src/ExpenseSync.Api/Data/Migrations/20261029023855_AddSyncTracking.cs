using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseSync.Api.Data.Migrations;

/// <inheritdoc />
public partial class _20261029023855_AddSyncTracking : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "Expenses",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "LastDeviceId",
            table: "Expenses",
            type: "TEXT",
            maxLength: 100,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<DateTime>(
            name: "LastModifiedUtc",
            table: "Expenses",
            type: "TEXT",
            nullable: false,
            defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

        migrationBuilder.CreateTable(
            name: "ReceivedChanges",
            columns: table => new
            {
                PendingChangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                ExpenseId = table.Column<Guid>(type: "TEXT", nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Operation = table.Column<int>(type: "INTEGER", nullable: false),
                CapturedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                ExpenseDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                LastModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsProcessed = table.Column<bool>(type: "INTEGER", nullable: false),
                ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReceivedChanges", x => x.PendingChangeId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReceivedChanges_IsProcessed",
            table: "ReceivedChanges",
            column: "IsProcessed");

        migrationBuilder.CreateIndex(
            name: "IX_ReceivedChanges_IsProcessed_CapturedAtUtc",
            table: "ReceivedChanges",
            columns: new[] { "IsProcessed", "CapturedAtUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ReceivedChanges");

        migrationBuilder.DropColumn(
            name: "IsDeleted",
            table: "Expenses");

        migrationBuilder.DropColumn(
            name: "LastDeviceId",
            table: "Expenses");

        migrationBuilder.DropColumn(
            name: "LastModifiedUtc",
            table: "Expenses");
    }
}
