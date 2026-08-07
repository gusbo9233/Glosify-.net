using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptSegmentStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_ProviderEventKey",
                table: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Sequence",
                table: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.AddColumn<string>(
                name: "Stream",
                table: "RealtimeTranslationTranscriptSegments",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "source");

            // Every existing segment belongs to a single-stream transcript: new ones
            // hold source speech, legacy ones hold translations.
            migrationBuilder.Sql(
                """
                UPDATE segment
                SET segment.[Stream] = transcript.[Stream]
                FROM [RealtimeTranslationTranscriptSegments] AS segment
                INNER JOIN [RealtimeTranslationTranscripts] AS transcript
                    ON transcript.[Id] = segment.[TranscriptId];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Stream_ProviderEventKey",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "SessionId", "Stream", "ProviderEventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Stream_Sequence",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "SessionId", "Stream", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_TranscriptId_Stream_CapturedAt",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "TranscriptId", "Stream", "CapturedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RealtimeTranslationTranscriptSegments_Stream",
                table: "RealtimeTranslationTranscriptSegments",
                sql: "[Stream] IN ('translation', 'source')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Stream_ProviderEventKey",
                table: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Stream_Sequence",
                table: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_TranscriptId_Stream_CapturedAt",
                table: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RealtimeTranslationTranscriptSegments_Stream",
                table: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropColumn(
                name: "Stream",
                table: "RealtimeTranslationTranscriptSegments");

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
        }
    }
}
