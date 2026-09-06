using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations;

public sealed partial class GroupSavedTranslationsIntoSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_SavedTranslations_AspNetUsers_UserId",
            table: "SavedTranslations");

        migrationBuilder.AddColumn<Guid>(
            name: "SessionId",
            table: "SavedTranslations",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "SavedTranslationSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                ClientSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SavedTranslationSessions", item => item.Id);
                table.ForeignKey(
                    name: "FK_SavedTranslationSessions_AspNetUsers_UserId",
                    column: item => item.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // History created before translation sessions existed remains available as a
        // one-entry session. Reusing the row id keeps old /Translations/{id} links valid.
        migrationBuilder.Sql(
            """
            INSERT INTO [SavedTranslationSessions]
                ([Id], [UserId], [ClientSessionId], [Title], [CreatedAt], [UpdatedAt])
            SELECT
                [Id],
                [UserId],
                [RequestId],
                LEFT(REPLACE(REPLACE([SourceText], CHAR(13), ' '), CHAR(10), ' '), 160),
                [CreatedAt],
                [CreatedAt]
            FROM [SavedTranslations];

            UPDATE [SavedTranslations]
            SET [SessionId] = [Id];
            """);

        migrationBuilder.AlterColumn<Guid>(
            name: "SessionId",
            table: "SavedTranslations",
            type: "uniqueidentifier",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uniqueidentifier",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SavedTranslations_SessionId_CreatedAt",
            table: "SavedTranslations",
            columns: ["SessionId", "CreatedAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_SavedTranslationSessions_UserId_ClientSessionId",
            table: "SavedTranslationSessions",
            columns: ["UserId", "ClientSessionId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SavedTranslationSessions_UserId_UpdatedAt",
            table: "SavedTranslationSessions",
            columns: ["UserId", "UpdatedAt"]);

        migrationBuilder.AddForeignKey(
            name: "FK_SavedTranslations_AspNetUsers_UserId",
            table: "SavedTranslations",
            column: "UserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);

        migrationBuilder.AddForeignKey(
            name: "FK_SavedTranslations_SavedTranslationSessions_SessionId",
            table: "SavedTranslations",
            column: "SessionId",
            principalTable: "SavedTranslationSessions",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_SavedTranslations_AspNetUsers_UserId",
            table: "SavedTranslations");

        migrationBuilder.DropForeignKey(
            name: "FK_SavedTranslations_SavedTranslationSessions_SessionId",
            table: "SavedTranslations");

        migrationBuilder.DropIndex(
            name: "IX_SavedTranslations_SessionId_CreatedAt",
            table: "SavedTranslations");

        migrationBuilder.DropColumn(
            name: "SessionId",
            table: "SavedTranslations");

        migrationBuilder.DropTable(name: "SavedTranslationSessions");

        migrationBuilder.AddForeignKey(
            name: "FK_SavedTranslations_AspNetUsers_UserId",
            table: "SavedTranslations",
            column: "UserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
