using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarUsageOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AvatarUsageOperation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PeriodKey = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    CreditRate = table.Column<decimal>(type: "decimal(28,15)", precision: 28, scale: 15, nullable: false),
                    ProviderSekRate = table.Column<decimal>(type: "decimal(28,15)", precision: 28, scale: 15, nullable: false),
                    ReservedUnits = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    SubmittedUnits = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    ReservedCredits = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    ReservedMicros = table.Column<long>(type: "bigint", nullable: false),
                    Settled = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvatarUsageOperation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarUsageOperation_Settled_ExpiresAt",
                table: "AvatarUsageOperation",
                columns: new[] { "Settled", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarUsageOperation_UserId_SessionId",
                table: "AvatarUsageOperation",
                columns: new[] { "UserId", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AvatarUsageOperation");
        }
    }
}
