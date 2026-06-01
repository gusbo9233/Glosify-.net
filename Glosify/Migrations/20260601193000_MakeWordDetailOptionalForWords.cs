using System;
using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(GlosifyContext))]
    [Migration("20260601193000_MakeWordDetailOptionalForWords")]
    public partial class MakeWordDetailOptionalForWords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_words_word_details",
                table: "words");

            migrationBuilder.AlterColumn<string>(
                name: "word_detail_id",
                table: "words",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AddForeignKey(
                name: "FK_words_word_details",
                table: "words",
                column: "word_detail_id",
                principalTable: "word_details",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Cannot safely make words.word_detail_id required after words without details have been allowed.");
        }
    }
}
