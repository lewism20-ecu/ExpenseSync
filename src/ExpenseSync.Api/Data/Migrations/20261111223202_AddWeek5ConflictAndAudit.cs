using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseSync.Api.Data.Migrations;

/// <inheritdoc />
public partial class _20261111223202_AddWeek5ConflictAndAudit : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Result",
            table: "ReceivedChanges",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ServerLastModifiedUtc",
            table: "ReceivedChanges",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AuditEntries",
            columns: table => new
            {
                AuditEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                ExpenseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Operation = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Details = table.Column<string>(type: "TEXT", nullable: false),
                ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEntries", x => x.AuditEntryId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditEntries");

        migrationBuilder.DropColumn(
            name: "Result",
            table: "ReceivedChanges");

        migrationBuilder.DropColumn(
            name: "ServerLastModifiedUtc",
            table: "ReceivedChanges");
    }
}
