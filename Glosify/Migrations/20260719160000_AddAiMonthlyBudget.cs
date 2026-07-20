using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations;

[DbContext(typeof(GlosifyContext))]
[Migration("20260719160000_AddAiMonthlyBudget")]
public partial class AddAiMonthlyBudget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "BudgetAmountMicros",
            table: "AiCreditTransactions",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BudgetPeriodKey",
            table: "AiCreditTransactions",
            type: "nvarchar(7)",
            maxLength: 7,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AiMonthlyBudgets",
            columns: table => new
            {
                PeriodKey = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                LimitMicros = table.Column<long>(type: "bigint", nullable: false),
                SpentMicros = table.Column<long>(type: "bigint", nullable: false),
                ReservedMicros = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RowVersion = table.Column<byte[]>(
                    type: "rowversion",
                    rowVersion: true,
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AiMonthlyBudgets", budget => budget.PeriodKey);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AiCreditTransactions_BudgetPeriodKey",
            table: "AiCreditTransactions",
            column: "BudgetPeriodKey");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AiMonthlyBudgets");

        migrationBuilder.DropIndex(
            name: "IX_AiCreditTransactions_BudgetPeriodKey",
            table: "AiCreditTransactions");

        migrationBuilder.DropColumn(
            name: "BudgetAmountMicros",
            table: "AiCreditTransactions");

        migrationBuilder.DropColumn(
            name: "BudgetPeriodKey",
            table: "AiCreditTransactions");
    }
}
