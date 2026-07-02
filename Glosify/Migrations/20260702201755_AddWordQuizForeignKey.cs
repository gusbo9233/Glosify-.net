using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddWordQuizForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Words never had a foreign key, so rows orphaned by earlier quiz
            // deletions may exist; they must go before the constraint can be created.
            migrationBuilder.Sql(
                "DELETE FROM words WHERE quiz_id NOT IN (SELECT [Id] FROM [Quizzes]);");

            migrationBuilder.CreateIndex(
                name: "IX_words_quiz_id",
                table: "words",
                column: "quiz_id");

            migrationBuilder.AddForeignKey(
                name: "FK_words_quizzes",
                table: "words",
                column: "quiz_id",
                principalTable: "Quizzes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_words_quizzes",
                table: "words");

            migrationBuilder.DropIndex(
                name: "IX_words_quiz_id",
                table: "words");
        }
    }
}
