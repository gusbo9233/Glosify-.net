using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantAnalyticsCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "turn_id",
                table: "assistant_messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantTelemetrySubjectId",
                table: "AspNetUsers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantTurnId",
                table: "AiCreditTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "AiCreditTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assistant_telemetry_deletion_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    table_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    dimension_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    dimension_value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "int", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    azure_operation_id = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_telemetry_deletion_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assistant_turns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    thread_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    profile = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    requested_model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    actual_model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    error_category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    server_duration_ms = table.Column<double>(type: "float", nullable: true),
                    client_duration_ms = table.Column<double>(type: "float", nullable: true),
                    tool_call_count = table.Column<int>(type: "int", nullable: false),
                    proposed_change_count = table.Column<int>(type: "int", nullable: false),
                    change_outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    change_outcome_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    final_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    trace_id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    provider_response_id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_turns", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantTurns_AssistantThreads_ThreadId",
                        column: x => x.thread_id,
                        principalTable: "assistant_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    turn_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rating = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_feedback", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantFeedback_AssistantTurns_TurnId",
                        column: x => x.turn_id,
                        principalTable: "assistant_turns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_model_invocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    turn_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    agent_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    agent_version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    profile = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    requested_model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    actual_model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    request_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    response_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    provider_response_id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    error_category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    duration_ms = table.Column<double>(type: "float", nullable: true),
                    prompt_tokens = table.Column<int>(type: "int", nullable: true),
                    candidate_tokens = table.Column<int>(type: "int", nullable: true),
                    thought_tokens = table.Column<int>(type: "int", nullable: true),
                    tool_prompt_tokens = table.Column<int>(type: "int", nullable: true),
                    total_tokens = table.Column<int>(type: "int", nullable: true),
                    trace_id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    span_id = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_model_invocations", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantModelInvocations_AssistantTurns_TurnId",
                        column: x => x.turn_id,
                        principalTable: "assistant_turns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_feedback_reasons",
                columns: table => new
                {
                    feedback_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reason_code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_feedback_reasons", x => new { x.feedback_id, x.reason_code });
                    table.ForeignKey(
                        name: "FK_AssistantFeedbackReasons_AssistantFeedback_FeedbackId",
                        column: x => x.feedback_id,
                        principalTable: "assistant_feedback",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_tool_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    turn_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    invocation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    tool_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    arguments_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    result_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    error_category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    duration_ms = table.Column<double>(type: "float", nullable: true),
                    proposed_change_count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_tool_executions", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantToolExecutions_AssistantModelInvocations_InvocationId",
                        column: x => x.invocation_id,
                        principalTable: "assistant_model_invocations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_messages_turn_id",
                table: "assistant_messages",
                column: "turn_id");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_AssistantTelemetrySubjectId",
                table: "AspNetUsers",
                column: "AssistantTelemetrySubjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_AssistantTurnId",
                table: "AiCreditTransactions",
                column: "AssistantTurnId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_OperationId",
                table: "AiCreditTransactions",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_feedback_turn_id",
                table: "assistant_feedback",
                column: "turn_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_model_invocations_provider_response_id",
                table: "assistant_model_invocations",
                column: "provider_response_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_model_invocations_trace_id",
                table: "assistant_model_invocations",
                column: "trace_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_model_invocations_turn_id_sequence",
                table: "assistant_model_invocations",
                columns: new[] { "turn_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_telemetry_deletion_requests_status_next_attempt_at",
                table: "assistant_telemetry_deletion_requests",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_telemetry_deletion_requests_table_name_dimension_name_dimension_value_status",
                table: "assistant_telemetry_deletion_requests",
                columns: new[] { "table_name", "dimension_name", "dimension_value", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tool_executions_invocation_id",
                table: "assistant_tool_executions",
                column: "invocation_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tool_executions_turn_id_sequence",
                table: "assistant_tool_executions",
                columns: new[] { "turn_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_turns_provider_actual_model_started_at",
                table: "assistant_turns",
                columns: new[] { "provider", "actual_model", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_turns_status_started_at",
                table: "assistant_turns",
                columns: new[] { "status", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_turns_thread_id_started_at",
                table: "assistant_turns",
                columns: new[] { "thread_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_turns_trace_id",
                table: "assistant_turns",
                column: "trace_id");

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantMessages_AssistantTurns_TurnId",
                table: "assistant_messages",
                column: "turn_id",
                principalTable: "assistant_turns",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantMessages_AssistantTurns_TurnId",
                table: "assistant_messages");

            migrationBuilder.DropTable(
                name: "assistant_feedback_reasons");

            migrationBuilder.DropTable(
                name: "assistant_telemetry_deletion_requests");

            migrationBuilder.DropTable(
                name: "assistant_tool_executions");

            migrationBuilder.DropTable(
                name: "assistant_feedback");

            migrationBuilder.DropTable(
                name: "assistant_model_invocations");

            migrationBuilder.DropTable(
                name: "assistant_turns");

            migrationBuilder.DropIndex(
                name: "IX_assistant_messages_turn_id",
                table: "assistant_messages");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_AssistantTelemetrySubjectId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AiCreditTransactions_AssistantTurnId",
                table: "AiCreditTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AiCreditTransactions_OperationId",
                table: "AiCreditTransactions");

            migrationBuilder.DropColumn(
                name: "turn_id",
                table: "assistant_messages");

            migrationBuilder.DropColumn(
                name: "AssistantTelemetrySubjectId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AssistantTurnId",
                table: "AiCreditTransactions");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "AiCreditTransactions");
        }
    }
}
