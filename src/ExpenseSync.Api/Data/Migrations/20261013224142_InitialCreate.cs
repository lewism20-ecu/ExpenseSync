using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseSync.Api.Data.Migrations;

/// <inheritdoc />
public partial class _20261013224142_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Expenses",
            columns: table => new
            {
                ExpenseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                ExpenseDate = table.Column<DateOnly>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Expenses", x => x.ExpenseId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Expenses");
    }
}
