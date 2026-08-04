using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedRealtimeTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TranscriptConsentAt",
                table: "RealtimeTranslationSessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TranscriptId",
                table: "RealtimeTranslationSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "context_transcript_id",
                table: "assistant_threads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationTranscripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationTranscripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationTranscripts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationTranscriptSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TranscriptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ProviderEventKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationTranscriptSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationTranscriptSegments_RealtimeTranslationTranscripts_TranscriptId",
                        column: x => x.TranscriptId,
                        principalTable: "RealtimeTranslationTranscripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationSessions_TranscriptId",
                table: "RealtimeTranslationSessions",
                column: "TranscriptId");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_context_transcript_id",
                table: "assistant_threads",
                column: "context_transcript_id");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscripts_UserId_UpdatedAt",
                table: "RealtimeTranslationTranscripts",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_ProviderEventKey",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "SessionId", "ProviderEventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Sequence",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_TranscriptId_CapturedAt",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "TranscriptId", "CapturedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantThreads_RealtimeTranslationTranscripts_ContextTranscriptId",
                table: "assistant_threads",
                column: "context_transcript_id",
                principalTable: "RealtimeTranslationTranscripts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_RealtimeTranslationSessions_RealtimeTranslationTranscripts_TranscriptId",
                table: "RealtimeTranslationSessions",
                column: "TranscriptId",
                principalTable: "RealtimeTranslationTranscripts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantThreads_RealtimeTranslationTranscripts_ContextTranscriptId",
                table: "assistant_threads");

            migrationBuilder.DropForeignKey(
                name: "FK_RealtimeTranslationSessions_RealtimeTranslationTranscripts_TranscriptId",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropTable(
                name: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropTable(
                name: "RealtimeTranslationTranscripts");

            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationSessions_TranscriptId",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_context_transcript_id",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "TranscriptConsentAt",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "TranscriptId",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "context_transcript_id",
                table: "assistant_threads");
        }
    }
}
