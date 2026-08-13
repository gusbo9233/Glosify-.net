using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantTurnRoutingCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_tools",
                table: "assistant_turns",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "intent_artifact",
                table: "assistant_turns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "intent_content",
                table: "assistant_turns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_version",
                table: "assistant_turns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allowed_tools",
                table: "assistant_turns");

            migrationBuilder.DropColumn(
                name: "intent_artifact",
                table: "assistant_turns");

            migrationBuilder.DropColumn(
                name: "intent_content",
                table: "assistant_turns");

            migrationBuilder.DropColumn(
                name: "prompt_version",
                table: "assistant_turns");
        }
    }
}
