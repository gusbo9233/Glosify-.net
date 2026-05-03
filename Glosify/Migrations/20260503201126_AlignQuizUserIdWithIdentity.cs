using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AlignQuizUserIdWithIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Quizzes",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            // Drop quizzes (and their words) whose UserId no longer matches an Identity user.
            // The previous Guid column let orphans accumulate because there was no FK; without this
            // cleanup the AddForeignKey call below would fail.
            migrationBuilder.Sql("""
                DELETE w
                FROM dbo.words w
                INNER JOIN dbo.Quizzes q ON q.Id = w.quiz_id
                WHERE NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers u WHERE u.Id = q.UserId);

                DELETE q
                FROM dbo.Quizzes q
                WHERE NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers u WHERE u.Id = q.UserId);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Quizzes_UserId",
                table: "Quizzes",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Quizzes_AspNetUsers_UserId",
                table: "Quizzes",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Quizzes_AspNetUsers_UserId",
                table: "Quizzes");

            migrationBuilder.DropIndex(
                name: "IX_Quizzes_UserId",
                table: "Quizzes");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "Quizzes",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);
        }
    }
}
