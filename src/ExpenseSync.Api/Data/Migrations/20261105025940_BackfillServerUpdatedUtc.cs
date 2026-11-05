using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseSync.Api.Data.Migrations;

/// <inheritdoc />
public partial class _20261105025940_BackfillServerUpdatedUtc : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "Expenses"
            SET "ServerUpdatedUtc" = "LastModifiedUtc"
            WHERE "ServerUpdatedUtc" = '0001-01-01 00:00:00';
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {

    }
}
