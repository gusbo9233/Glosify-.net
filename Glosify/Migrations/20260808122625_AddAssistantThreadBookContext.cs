using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantThreadBookContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "context_book_document_id",
                table: "assistant_threads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_context_book_document_id",
                table: "assistant_threads",
                column: "context_book_document_id");

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantThreads_BookDocuments_ContextBookDocumentId",
                table: "assistant_threads",
                column: "context_book_document_id",
                principalTable: "BookDocuments",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantThreads_BookDocuments_ContextBookDocumentId",
                table: "assistant_threads");

            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_context_book_document_id",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "context_book_document_id",
                table: "assistant_threads");
        }
    }
}
