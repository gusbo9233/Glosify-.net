using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(GlosifyContext))]
    [Migration("20260610120000_RemoveWordDetails")]
    public partial class RemoveWordDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_words_word_details",
                table: "words");

            migrationBuilder.DropIndex(
                name: "IX_words_word_detail_id",
                table: "words");

            migrationBuilder.DropColumn(
                name: "word_detail_id",
                table: "words");

            migrationBuilder.DropTable(
                name: "word_details");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Word details were removed in favor of external Wiktionary links.");
        }
    }
}
