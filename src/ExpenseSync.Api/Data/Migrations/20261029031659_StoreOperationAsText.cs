using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseSync.Api.Data.Migrations;

/// <inheritdoc />
public partial class _20261029031659_StoreOperationAsText : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Operation",
            table: "ReceivedChanges",
            type: "TEXT",
            maxLength: 10,
            nullable: false,
            oldClrType: typeof(int),
            oldType: "INTEGER");

        // Rows received before this migration hold the enum's number; convert them to its name.
        migrationBuilder.Sql(
            """
            UPDATE ReceivedChanges SET Operation = CASE Operation
                WHEN '0' THEN 'Create'
                WHEN '1' THEN 'Update'
                WHEN '2' THEN 'Delete'
                ELSE Operation END;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE ReceivedChanges SET Operation = CASE Operation
                WHEN 'Create' THEN '0'
                WHEN 'Update' THEN '1'
                WHEN 'Delete' THEN '2'
                ELSE Operation END;
            """);

        migrationBuilder.AlterColumn<int>(
            name: "Operation",
            table: "ReceivedChanges",
            type: "INTEGER",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 10);
    }
}
