using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeTranslationSpeechProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpeechProvider",
                table: "RealtimeTranslationSessions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "foundry");

            migrationBuilder.Sql(
                "UPDATE [RealtimeTranslationSessions] SET [SpeechProvider] = 'azure' WHERE [TranslationMode] = 'economical'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeechProvider",
                table: "RealtimeTranslationSessions");
        }
    }
}
