using System;
using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    [DbContext(typeof(GlosifyContext))]
    [Migration("20260426130000_AddDictionaryEntries")]
    public partial class AddDictionaryEntries : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dictionary_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    word = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    lang_code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    part_of_speech = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    properties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    variants = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    example_sentence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "dictionary_entries");
        }
    }
}
