using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedSourceTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscripts_UserId_UpdatedAt",
                table: "RealtimeTranslationTranscripts");

            migrationBuilder.AddColumn<string>(
                name: "Stream",
                table: "RealtimeTranslationTranscripts",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "translation");

            migrationBuilder.AddColumn<string>(
                name: "BillingModel",
                table: "RealtimeTranslationSessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CreditsPerStartedMinute",
                table: "RealtimeTranslationSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceTranscriptionDeployment",
                table: "RealtimeTranslationSessions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectedQuizLanguageCode",
                table: "AspNetUsers",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE [RealtimeTranslationSessions] SET [BillingModel] = [Model] WHERE [BillingModel] = '';");
            migrationBuilder.Sql(
                "UPDATE [RealtimeTranslationSessions] SET [CreditsPerStartedMinute] = 8 WHERE [CreditsPerStartedMinute] = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscripts_UserId_TargetLanguage_UpdatedAt",
                table: "RealtimeTranslationTranscripts",
                columns: new[] { "UserId", "TargetLanguage", "UpdatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RealtimeTranslationTranscripts_Stream",
                table: "RealtimeTranslationTranscripts",
                sql: "[Stream] IN ('translation', 'source')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_SelectedQuizLanguageCode",
                table: "AspNetUsers",
                sql: "[SelectedQuizLanguageCode] IS NULL OR [SelectedQuizLanguageCode] IN ('et', 'de', 'pl', 'uk')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RealtimeTranslationTranscripts_UserId_TargetLanguage_UpdatedAt",
                table: "RealtimeTranslationTranscripts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RealtimeTranslationTranscripts_Stream",
                table: "RealtimeTranslationTranscripts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_SelectedQuizLanguageCode",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Stream",
                table: "RealtimeTranslationTranscripts");

            migrationBuilder.DropColumn(
                name: "BillingModel",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "CreditsPerStartedMinute",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "SourceTranscriptionDeployment",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "SelectedQuizLanguageCode",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscripts_UserId_UpdatedAt",
                table: "RealtimeTranslationTranscripts",
                columns: new[] { "UserId", "UpdatedAt" });
        }
    }
}
