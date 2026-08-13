using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantTurnLanguageAndOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "intent_operation",
                table: "assistant_turns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reply_language",
                table: "assistant_turns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_language",
                table: "assistant_turns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_language",
                table: "assistant_turns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_turns_target_language_started_at",
                table: "assistant_turns",
                columns: new[] { "target_language", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_turns_target_language_started_at",
                table: "assistant_turns");

            migrationBuilder.DropColumn(
                name: "intent_operation",
                table: "assistant_turns");

            migrationBuilder.DropColumn(
                name: "reply_language",
                table: "assistant_turns");

            migrationBuilder.DropColumn(
                name: "source_language",
                table: "assistant_turns");

            migrationBuilder.DropColumn(
                name: "target_language",
                table: "assistant_turns");
        }
    }
}
