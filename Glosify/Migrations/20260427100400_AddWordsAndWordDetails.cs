using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddWordsAndWordDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[word_details]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [word_details] (
                        [id] nvarchar(450) NOT NULL,
                        [quiz_id] uniqueidentifier NOT NULL,
                        [properties] nvarchar(max) NOT NULL,
                        [example_sentence] nvarchar(max) NOT NULL,
                        [explanation] nvarchar(max) NOT NULL,
                        [variants] nvarchar(max) NOT NULL,
                        [language] nvarchar(max) NOT NULL,
                        CONSTRAINT [PK_word_details] PRIMARY KEY ([id])
                    );
                END
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[words]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [words] (
                        [id] nvarchar(450) NOT NULL,
                        [quiz_id] uniqueidentifier NOT NULL,
                        [lemma] nvarchar(max) NOT NULL,
                        [translation] nvarchar(max) NOT NULL,
                        [word_detail_id] nvarchar(max) NOT NULL,
                        CONSTRAINT [PK_words] PRIMARY KEY ([id])
                    );
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "word_details");

            migrationBuilder.DropTable(
                name: "words");
        }
    }
}
