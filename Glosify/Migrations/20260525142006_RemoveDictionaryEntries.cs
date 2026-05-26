using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDictionaryEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dictionary_entries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dictionary_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    example_sentence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    lang_code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    part_of_speech = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    properties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    source_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    variants = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    word = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictionary_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dictionary_entries_lang_code_word",
                table: "dictionary_entries",
                columns: new[] { "lang_code", "word" });

            migrationBuilder.CreateIndex(
                name: "IX_dictionary_entries_source_hash",
                table: "dictionary_entries",
                column: "source_hash",
                unique: true);
        }
    }
}
