using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddQuizTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID('dbo.Quizzes', 'U') IS NOT NULL DROP TABLE dbo.Quizzes");

            migrationBuilder.CreateTable(
                name: "Quizzes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsSongQuiz = table.Column<bool>(type: "bit", nullable: false),
                    ProcessingStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProcessingMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceLanguage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnkiTrackingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackWordsForward = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackWordsReverse = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackSentencesForward = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackSentencesReverse = table.Column<bool>(type: "bit", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    OriginalQuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quizzes", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Quizzes");
        }
    }
}
