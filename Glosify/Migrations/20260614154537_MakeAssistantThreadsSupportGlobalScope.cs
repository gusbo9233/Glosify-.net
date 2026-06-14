using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class MakeAssistantThreadsSupportGlobalScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_user_id",
                table: "assistant_threads");

            migrationBuilder.AlterColumn<Guid>(
                name: "quiz_id",
                table: "assistant_threads",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_user_id_quiz_id",
                table: "assistant_threads",
                columns: new[] { "user_id", "quiz_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_user_id_quiz_id",
                table: "assistant_threads");

            migrationBuilder.AlterColumn<Guid>(
                name: "quiz_id",
                table: "assistant_threads",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_user_id",
                table: "assistant_threads",
                column: "user_id");
        }
    }
}
