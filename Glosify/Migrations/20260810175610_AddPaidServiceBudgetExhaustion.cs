using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddPaidServiceBudgetExhaustion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExhaustedAt",
                table: "AiMonthlyBudgets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExhaustedReason",
                table: "AiMonthlyBudgets",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OverrunMicros",
                table: "AiMonthlyBudgets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExhaustedAt",
                table: "AiMonthlyBudgets");

            migrationBuilder.DropColumn(
                name: "ExhaustedReason",
                table: "AiMonthlyBudgets");

            migrationBuilder.DropColumn(
                name: "OverrunMicros",
                table: "AiMonthlyBudgets");
        }
    }
}
