using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantSavedChatContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "context_quiz_id",
                table: "assistant_threads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "context_quiz_id",
                table: "assistant_messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_context_quiz_id",
                table: "assistant_threads",
                column: "context_quiz_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_messages_context_quiz_id",
                table: "assistant_messages",
                column: "context_quiz_id");

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantMessages_Quizzes_ContextQuizId",
                table: "assistant_messages",
                column: "context_quiz_id",
                principalTable: "Quizzes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantThreads_Quizzes_ContextQuizId",
                table: "assistant_threads",
                column: "context_quiz_id",
                principalTable: "Quizzes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantMessages_Quizzes_ContextQuizId",
                table: "assistant_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_AssistantThreads_Quizzes_ContextQuizId",
                table: "assistant_threads");

            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_context_quiz_id",
                table: "assistant_threads");

            migrationBuilder.DropIndex(
                name: "IX_assistant_messages_context_quiz_id",
                table: "assistant_messages");

            migrationBuilder.DropColumn(
                name: "context_quiz_id",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "context_quiz_id",
                table: "assistant_messages");
        }
    }
}
