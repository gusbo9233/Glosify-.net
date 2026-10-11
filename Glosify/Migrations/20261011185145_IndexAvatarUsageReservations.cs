using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class IndexAvatarUsageReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_AvatarReservations_CreatedAt",
                table: "AiCreditTransactions",
                column: "CreatedAt",
                filter: "[Kind] = 'reservation' AND [RelatedEntityType] = 'AvatarSession' AND [RelatedEntityId] IS NOT NULL AND [ReservationId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "UserId", "ReservationId", "RelatedEntityId", "Provider", "Model", "Operation" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiCreditTransactions_AvatarReservations_CreatedAt",
                table: "AiCreditTransactions");
        }
    }
}
