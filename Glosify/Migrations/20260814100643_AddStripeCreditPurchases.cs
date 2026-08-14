using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeCreditPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StripeCreditPurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PackageKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PriceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StripeCheckoutSessionId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    LastWebhookEventId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeCreditPurchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripeCreditPurchases_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_RelatedEntityType_RelatedEntityId",
                table: "AiCreditTransactions",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" },
                unique: true,
                filter: "[Kind] = 'stripe_purchase' AND [RelatedEntityType] = 'StripeCreditPurchase' AND [RelatedEntityId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StripeCreditPurchases_StripeCheckoutSessionId",
                table: "StripeCreditPurchases",
                column: "StripeCheckoutSessionId",
                unique: true,
                filter: "[StripeCheckoutSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StripeCreditPurchases_UserId_CreatedAt",
                table: "StripeCreditPurchases",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripeCreditPurchases");

            migrationBuilder.DropIndex(
                name: "IX_AiCreditTransactions_RelatedEntityType_RelatedEntityId",
                table: "AiCreditTransactions");
        }
    }
}
