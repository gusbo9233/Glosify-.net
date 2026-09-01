using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations;

[DbContext(typeof(GlosifyContext))]
[Migration("20260901095356_AddSavedTranslations")]
public sealed class AddSavedTranslations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SavedTranslations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SourceLanguage = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                DetectedSourceLanguage = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                TargetLanguage = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                Preferences = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                SourceText = table.Column<string>(type: "nvarchar(max)", maxLength: 8_000, nullable: false),
                TranslatedText = table.Column<string>(type: "nvarchar(max)", maxLength: 16_000, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SavedTranslations", item => item.Id);
                table.ForeignKey(
                    name: "FK_SavedTranslations_AspNetUsers_UserId",
                    column: item => item.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SavedTranslations_UserId_CreatedAt",
            table: "SavedTranslations",
            columns: ["UserId", "CreatedAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_SavedTranslations_UserId_RequestId",
            table: "SavedTranslations",
            columns: ["UserId", "RequestId"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "SavedTranslations");
}
