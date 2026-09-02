using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class BindSavedTranslationsToLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedTranslationSessions_UserId_ClientSessionId",
                table: "SavedTranslationSessions");

            migrationBuilder.DropIndex(
                name: "IX_SavedTranslationSessions_UserId_UpdatedAt",
                table: "SavedTranslationSessions");

            migrationBuilder.AddColumn<string>(
                name: "LanguageCode",
                table: "SavedTranslationSessions",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE sessions
                SET LanguageCode = COALESCE(
                    (
                        SELECT TOP(1) translations.TargetLanguage
                        FROM SavedTranslations AS translations
                        WHERE translations.SessionId = sessions.Id
                        ORDER BY translations.CreatedAt, translations.Id
                    ),
                    'en')
                FROM SavedTranslationSessions AS sessions;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "LanguageCode",
                table: "SavedTranslationSessions",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(8)",
                oldMaxLength: 8,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedTranslationSessions_UserId_ClientSessionId_LanguageCode",
                table: "SavedTranslationSessions",
                columns: new[] { "UserId", "ClientSessionId", "LanguageCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedTranslationSessions_UserId_LanguageCode_UpdatedAt",
                table: "SavedTranslationSessions",
                columns: new[] { "UserId", "LanguageCode", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedTranslationSessions_UserId_ClientSessionId_LanguageCode",
                table: "SavedTranslationSessions");

            migrationBuilder.DropIndex(
                name: "IX_SavedTranslationSessions_UserId_LanguageCode_UpdatedAt",
                table: "SavedTranslationSessions");

            migrationBuilder.Sql("""
                UPDATE translations
                SET SessionId = canonical.Id
                FROM SavedTranslations AS translations
                INNER JOIN SavedTranslationSessions AS currentSession
                    ON currentSession.Id = translations.SessionId
                CROSS APPLY
                (
                    SELECT TOP(1) candidate.Id
                    FROM SavedTranslationSessions AS candidate
                    WHERE candidate.UserId = currentSession.UserId
                        AND candidate.ClientSessionId = currentSession.ClientSessionId
                    ORDER BY candidate.CreatedAt, candidate.Id
                ) AS canonical
                WHERE translations.SessionId <> canonical.Id;

                DELETE duplicateSession
                FROM SavedTranslationSessions AS duplicateSession
                CROSS APPLY
                (
                    SELECT TOP(1) candidate.Id
                    FROM SavedTranslationSessions AS candidate
                    WHERE candidate.UserId = duplicateSession.UserId
                        AND candidate.ClientSessionId = duplicateSession.ClientSessionId
                    ORDER BY candidate.CreatedAt, candidate.Id
                ) AS canonical
                WHERE duplicateSession.Id <> canonical.Id;
                """);

            migrationBuilder.DropColumn(
                name: "LanguageCode",
                table: "SavedTranslationSessions");

            migrationBuilder.CreateIndex(
                name: "IX_SavedTranslationSessions_UserId_ClientSessionId",
                table: "SavedTranslationSessions",
                columns: new[] { "UserId", "ClientSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedTranslationSessions_UserId_UpdatedAt",
                table: "SavedTranslationSessions",
                columns: new[] { "UserId", "UpdatedAt" });
        }
    }
}
