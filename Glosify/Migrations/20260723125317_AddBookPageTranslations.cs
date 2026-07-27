using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddBookPageTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreferredTranslationLanguage",
                table: "BookDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookPageTranslations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookPageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceTextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DetectedSourceLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SegmentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookPageTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookPageTranslations_BookPages_BookPageId",
                        column: x => x.BookPageId,
                        principalTable: "BookPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookPageTranslations_BookPageId_TargetLanguage_SourceTextHash_SchemaVersion",
                table: "BookPageTranslations",
                columns: new[] { "BookPageId", "TargetLanguage", "SourceTextHash", "SchemaVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookPageTranslations");

            migrationBuilder.DropColumn(
                name: "PreferredTranslationLanguage",
                table: "BookDocuments");
        }
    }
}
