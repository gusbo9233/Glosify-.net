using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddStandaloneQuizSentences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quiz_sentences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    translation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quiz_sentences", x => x.id);
                    table.ForeignKey(
                        name: "FK_quiz_sentences_quizzes",
                        column: x => x.quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quiz_sentences_quiz_id",
                table: "quiz_sentences",
                column: "quiz_id");

            migrationBuilder.Sql(
                """
                INSERT INTO [quiz_sentences] ([id], [quiz_id], [text], [translation], [created_at])
                SELECT NEWID(), [quiz_id], [text], COALESCE(MAX([translation]), N''), SYSDATETIMEOFFSET()
                FROM (
                    SELECT
                        w.[quiz_id],
                        NULLIF(LTRIM(RTRIM(w.[example_sentence])), N'') AS [text],
                        NULLIF(LTRIM(RTRIM(w.[example_sentence_translation])), N'') AS [translation]
                    FROM [words] AS w
                    WHERE NULLIF(LTRIM(RTRIM(w.[example_sentence])), N'') IS NOT NULL

                    UNION ALL

                    SELECT
                        w.[quiz_id],
                        NULLIF(LTRIM(RTRIM(d.[example_sentence])), N'') AS [text],
                        NULLIF(LTRIM(RTRIM(d.[example_sentence_translation])), N'') AS [translation]
                    FROM [words] AS w
                    INNER JOIN [word_details] AS d ON w.[word_detail_id] = d.[id]
                    WHERE NULLIF(LTRIM(RTRIM(d.[example_sentence])), N'') IS NOT NULL
                ) AS source
                WHERE [text] IS NOT NULL
                GROUP BY [quiz_id], [text];
                """);

            migrationBuilder.DropColumn(
                name: "example_sentence",
                table: "words");

            migrationBuilder.DropColumn(
                name: "example_sentence_translation",
                table: "words");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "example_sentence",
                table: "words",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "example_sentence_translation",
                table: "words",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.DropTable(
                name: "quiz_sentences");
        }
    }
}
