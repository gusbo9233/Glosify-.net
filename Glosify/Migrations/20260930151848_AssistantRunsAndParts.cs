using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AssistantRunsAndParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approval_rules",
                table: "assistant_threads",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cached_prompt_tokens",
                table: "assistant_model_invocations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assistant_parts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    step = table.Column<int>(type: "int", nullable: false),
                    type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    text = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    tool_name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    call_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    input_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    state = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    output = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    metadata_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    provider_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    compacted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_parts", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantParts_AssistantMessages_MessageId",
                        column: x => x.message_id,
                        principalTable: "assistant_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    thread_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    active_user_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    idempotency_key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    request_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    state_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    plan_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    turn_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    current_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    lease_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lease_until = table.Column<DateTime>(type: "datetime2", nullable: false),
                    retry_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    window_started_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    steps = table.Column<int>(type: "int", nullable: false),
                    total_steps = table.Column<int>(type: "int", nullable: false),
                    failures = table.Column<int>(type: "int", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cached_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    saved_changes = table.Column<int>(type: "int", nullable: false),
                    undone_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantRuns_AssistantThreads_ThreadId",
                        column: x => x.thread_id,
                        principalTable: "assistant_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    part_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    entity_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    before_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    after_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantChanges_AssistantRuns_RunId",
                        column: x => x.run_id,
                        principalTable: "assistant_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_tool_evaluations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    part_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tool_name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    snapshot_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    snapshot_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    result_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    evaluation_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_tool_evaluations", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantToolEvaluations_AssistantRuns_RunId",
                        column: x => x.run_id,
                        principalTable: "assistant_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_changes_run_id_sequence",
                table: "assistant_changes",
                columns: new[] { "run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_parts_message_id_sequence",
                table: "assistant_parts",
                columns: new[] { "message_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_parts_run_id_step",
                table: "assistant_parts",
                columns: new[] { "run_id", "step" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_runs_active_user_id",
                table: "assistant_runs",
                column: "active_user_id",
                unique: true,
                filter: "[active_user_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_runs_status_retry_at_lease_until",
                table: "assistant_runs",
                columns: new[] { "status", "retry_at", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_runs_thread_id_created_at",
                table: "assistant_runs",
                columns: new[] { "thread_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_runs_user_id_idempotency_key",
                table: "assistant_runs",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tool_evaluations_part_id",
                table: "assistant_tool_evaluations",
                column: "part_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tool_evaluations_run_id",
                table: "assistant_tool_evaluations",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tool_evaluations_status_created_at",
                table: "assistant_tool_evaluations",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_changes");

            migrationBuilder.DropTable(
                name: "assistant_parts");

            migrationBuilder.DropTable(
                name: "assistant_tool_evaluations");

            migrationBuilder.DropTable(
                name: "assistant_runs");

            migrationBuilder.DropColumn(
                name: "approval_rules",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "cached_prompt_tokens",
                table: "assistant_model_invocations");
        }
    }
}
