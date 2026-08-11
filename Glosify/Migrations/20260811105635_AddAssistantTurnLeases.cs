using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantTurnLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "active_turn_expires_at",
                table: "assistant_threads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "active_turn_id",
                table: "assistant_threads",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "active_turn_expires_at",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "active_turn_id",
                table: "assistant_threads");
        }
    }
}
