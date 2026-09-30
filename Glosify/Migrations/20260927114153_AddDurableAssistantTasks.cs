using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableAssistantTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActiveUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SteeringJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WindowStartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RetryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModelCalls = table.Column<int>(type: "int", nullable: false),
                    Tokens = table.Column<int>(type: "int", nullable: false),
                    Failures = table.Column<int>(type: "int", nullable: false),
                    SavedChanges = table.Column<int>(type: "int", nullable: false),
                    ManualApproval = table.Column<bool>(type: "bit", nullable: false),
                    ApprovalGranted = table.Column<bool>(type: "bit", nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantTasks_assistant_threads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "assistant_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantTaskCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ArgumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EvaluationStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EvaluationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTaskCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantTaskCalls_AssistantTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AssistantTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTaskCalls_TaskId_Sequence",
                table: "AssistantTaskCalls",
                columns: new[] { "TaskId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTasks_ActiveUserId",
                table: "AssistantTasks",
                column: "ActiveUserId",
                unique: true,
                filter: "[ActiveUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTasks_Status_RetryAt_LeaseUntil",
                table: "AssistantTasks",
                columns: new[] { "Status", "RetryAt", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTasks_ThreadId",
                table: "AssistantTasks",
                column: "ThreadId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTasks_UserId_IdempotencyKey",
                table: "AssistantTasks",
                columns: new[] { "UserId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantTaskCalls");

            migrationBuilder.DropTable(
                name: "AssistantTasks");
        }
    }
}
