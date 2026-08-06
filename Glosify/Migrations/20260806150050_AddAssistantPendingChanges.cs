using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantPendingChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_pending_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    context_quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_pending_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantPendingChanges_Quizzes_ContextQuizId",
                        column: x => x.context_quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_context_quiz_id",
                table: "assistant_pending_changes",
                column: "context_quiz_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_conversation_id_sequence",
                table: "assistant_pending_changes",
                columns: new[] { "conversation_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_message_id",
                table: "assistant_pending_changes",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_user_id_status",
                table: "assistant_pending_changes",
                columns: new[] { "user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_pending_changes");
        }
    }
}
