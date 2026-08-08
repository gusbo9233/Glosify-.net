using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantThreadLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_user_id_quiz_id",
                table: "assistant_threads");

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "assistant_threads",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // Chats saved before this point belong to no language, so guessing one for
            // them would file old conversations under a language they were never held
            // in. They are discarded instead: everyone starts fresh, per language.
            // The pending changes go first because they carry no foreign key of their
            // own, then the messages cascade with their thread.
            migrationBuilder.Sql(
                """
                DELETE FROM [assistant_pending_changes]
                WHERE [message_id] IN (SELECT [id] FROM [assistant_messages]);

                DELETE FROM [assistant_threads];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_user_id_quiz_id_language",
                table: "assistant_threads",
                columns: new[] { "user_id", "quiz_id", "language" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_user_id_quiz_id_language",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "language",
                table: "assistant_threads");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_user_id_quiz_id",
                table: "assistant_threads",
                columns: new[] { "user_id", "quiz_id" });
        }
    }
}
