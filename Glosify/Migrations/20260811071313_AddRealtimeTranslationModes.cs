using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeTranslationModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceLanguage",
                table: "RealtimeTranslationSessions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranslationMode",
                table: "RealtimeTranslationSessions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "enhanced");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceLanguage",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "TranslationMode",
                table: "RealtimeTranslationSessions");
        }
    }
}
