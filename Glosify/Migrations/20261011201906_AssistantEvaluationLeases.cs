using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AssistantEvaluationLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_tool_evaluations_status_created_at",
                table: "assistant_tool_evaluations");

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                table: "assistant_tool_evaluations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "assistant_tool_evaluations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tool_evaluations_status_lease_until_created_at",
                table: "assistant_tool_evaluations",
                columns: new[] { "status", "lease_until", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_tool_evaluations_status_lease_until_created_at",
                table: "assistant_tool_evaluations");

            migrationBuilder.DropColumn(
                name: "lease_id",
                table: "assistant_tool_evaluations");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "assistant_tool_evaluations");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tool_evaluations_status_created_at",
                table: "assistant_tool_evaluations",
                columns: new[] { "status", "created_at" });
        }
    }
}
