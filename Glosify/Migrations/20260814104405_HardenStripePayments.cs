using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class HardenStripePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "StripeCreditPurchases",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "HasUnresolvedDispute",
                table: "StripeCreditPurchases",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "RefundedAmountMinor",
                table: "StripeCreditPurchases",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "RevokedCredits",
                table: "StripeCreditPurchases",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "StripeCreditPurchases",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<long>(
                name: "UnitAmountMinor",
                table: "StripeCreditPurchases",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<string>(
                name: "RelatedEntityId",
                table: "AiCreditTransactions",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "StripePaymentEvents",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    StripeEventId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    PurchaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreditDelta = table.Column<int>(type: "int", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripePaymentEvents", x => x.IdempotencyKey);
                    table.ForeignKey(
                        name: "FK_StripePaymentEvents_StripeCreditPurchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "StripeCreditPurchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StripeCreditPurchases_StripePaymentIntentId",
                table: "StripeCreditPurchases",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "[StripePaymentIntentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_RelatedEntityType_RelatedEntityId_Kind",
                table: "AiCreditTransactions",
                columns: new[] { "RelatedEntityType", "RelatedEntityId", "Kind" },
                unique: true,
                filter: "[Kind] = 'stripe_adjustment' AND [RelatedEntityType] = 'StripePaymentEvent' AND [RelatedEntityId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StripePaymentEvents_PurchaseId",
                table: "StripePaymentEvents",
                column: "PurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_StripePaymentEvents_StripeEventId",
                table: "StripePaymentEvents",
                column: "StripeEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripePaymentEvents");

            migrationBuilder.DropIndex(
                name: "IX_StripeCreditPurchases_StripePaymentIntentId",
                table: "StripeCreditPurchases");

            migrationBuilder.DropIndex(
                name: "IX_AiCreditTransactions_RelatedEntityType_RelatedEntityId_Kind",
                table: "AiCreditTransactions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "StripeCreditPurchases");

            migrationBuilder.DropColumn(
                name: "HasUnresolvedDispute",
                table: "StripeCreditPurchases");

            migrationBuilder.DropColumn(
                name: "RefundedAmountMinor",
                table: "StripeCreditPurchases");

            migrationBuilder.DropColumn(
                name: "RevokedCredits",
                table: "StripeCreditPurchases");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "StripeCreditPurchases");

            migrationBuilder.DropColumn(
                name: "UnitAmountMinor",
                table: "StripeCreditPurchases");

            migrationBuilder.AlterColumn<string>(
                name: "RelatedEntityId",
                table: "AiCreditTransactions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);
        }
    }
}
