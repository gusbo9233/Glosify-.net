using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AvatarConversationTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Speaker",
                table: "RealtimeTranslationTranscriptSegments",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AvatarConversationId",
                table: "RealtimeTranslationTranscripts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "RealtimeTranslationTranscripts",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "subtitles");

            migrationBuilder.AddColumn<int>(
                name: "avatar_conversation_turn_count",
                table: "assistant_runs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "avatar_snapshot_id",
                table: "assistant_runs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoSaveAvatarConversations",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "assistant_avatar_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TurnCount = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_avatar_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assistant_avatar_snapshots_assistant_threads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "assistant_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscripts_AvatarConversationId",
                table: "RealtimeTranslationTranscripts",
                column: "AvatarConversationId",
                unique: true,
                filter: "[AvatarConversationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_avatar_snapshots_ThreadId_ContentHash",
                table: "assistant_avatar_snapshots",
                columns: new[] { "ThreadId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_avatar_snapshots_ThreadId_ConversationId_Language_CreatedAt",
                table: "assistant_avatar_snapshots",
                columns: new[] { "ThreadId", "ConversationId", "Language", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_avatar_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscripts_AvatarConversationId",
                table: "RealtimeTranslationTranscripts");

            migrationBuilder.DropColumn(
                name: "Speaker",
                table: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropColumn(
                name: "AvatarConversationId",
                table: "RealtimeTranslationTranscripts");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "RealtimeTranslationTranscripts");

            migrationBuilder.DropColumn(
                name: "avatar_conversation_turn_count",
                table: "assistant_runs");

            migrationBuilder.DropColumn(
                name: "avatar_snapshot_id",
                table: "assistant_runs");

            migrationBuilder.DropColumn(
                name: "AutoSaveAvatarConversations",
                table: "AspNetUsers");
        }
    }
}
