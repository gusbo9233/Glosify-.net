using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(GlosifyContext))]
    [Migration("20260509120000_StoreExampleSentenceTranslations")]
    public partial class StoreExampleSentenceTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "example_sentence_translation",
                table: "word_details",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE dbo.word_details
                SET
                    example_sentence_translation = explanation,
                    explanation = N''
                WHERE COALESCE(example_sentence, N'') <> N''
                    AND COALESCE(explanation, N'') <> N''
                    AND COALESCE(properties, N'{}') = N'{}'
                    AND COALESCE(variants, N'[]') = N'[]';

                UPDATE dbo.word_details
                SET
                    example_sentence = N'',
                    example_sentence_translation = N''
                WHERE COALESCE(example_sentence, N'') <> N''
                    AND (
                        CHARINDEX(LOWER(word), LOWER(example_sentence)) = 0
                        OR (
                            COALESCE(translation, N'') <> N''
                            AND CHARINDEX(LOWER(translation), LOWER(example_sentence)) > 0
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "example_sentence_translation",
                table: "word_details");
        }
    }
}
