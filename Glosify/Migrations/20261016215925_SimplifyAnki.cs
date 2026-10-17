using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyAnki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Anki becomes one collection per user and learning language. The owner chose
            // to start fresh rather than merge existing collections, so clear Anki data in
            // foreign-key order before the new unique index is created. Down restores the
            // schema only; deleted study data is not recoverable.
            migrationBuilder.Sql("DELETE FROM [AnkiReviews];");
            migrationBuilder.Sql("DELETE FROM [AnkiCards];");
            migrationBuilder.Sql("DELETE FROM [AnkiNotes];");
            migrationBuilder.Sql("DELETE FROM [AnkiQuizLinks];");
            migrationBuilder.Sql("DELETE FROM [AnkiCollections];");

            migrationBuilder.DropIndex(
                name: "IX_AnkiCollections_UserId_Name",
                table: "AnkiCollections");

            migrationBuilder.DropIndex(
                name: "IX_AnkiCollections_UserId_SourceLanguage_TargetLanguage",
                table: "AnkiCollections");

            migrationBuilder.DropColumn(
                name: "SourceLanguage",
                table: "AnkiCollections");

            migrationBuilder.AddColumn<string>(
                name: "SourceLanguage",
                table: "AnkiNotes",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AnkiCollections_UserId_TargetLanguage",
                table: "AnkiCollections",
                columns: new[] { "UserId", "TargetLanguage" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnkiCollections_UserId_TargetLanguage",
                table: "AnkiCollections");

            migrationBuilder.DropColumn(
                name: "SourceLanguage",
                table: "AnkiNotes");

            migrationBuilder.AddColumn<string>(
                name: "SourceLanguage",
                table: "AnkiCollections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AnkiCollections_UserId_Name",
                table: "AnkiCollections",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnkiCollections_UserId_SourceLanguage_TargetLanguage",
                table: "AnkiCollections",
                columns: new[] { "UserId", "SourceLanguage", "TargetLanguage" });
        }
    }
}
