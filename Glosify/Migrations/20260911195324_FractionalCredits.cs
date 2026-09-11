using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class FractionalCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ReservedAfterCredits",
                table: "AiCreditTransactions",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "CreditAmount",
                table: "AiCreditTransactions",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "BalanceAfterCredits",
                table: "AiCreditTransactions",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "ReservedCredits",
                table: "AiCreditAccounts",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "BalanceCredits",
                table: "AiCreditAccounts",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Fractional financial history cannot be rolled back to integers. Deploy a fractional-compatible application.");
        }
    }
}
