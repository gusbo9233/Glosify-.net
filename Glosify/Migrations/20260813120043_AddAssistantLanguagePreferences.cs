using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantLanguagePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conversation_language",
                table: "assistant_threads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredAssistantLanguage",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredSourceLanguage",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conversation_language",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "PreferredAssistantLanguage",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PreferredSourceLanguage",
                table: "AspNetUsers");
        }
    }
}
