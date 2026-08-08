using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddBookLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "BookDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // Books uploaded before the library was language-scoped are all Polish.
            migrationBuilder.Sql(
                """
                UPDATE [BookDocuments]
                SET [Language] = N'Polish'
                WHERE [Language] IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BookDocuments_UserId_Language",
                table: "BookDocuments",
                columns: new[] { "UserId", "Language" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookDocuments_UserId_Language",
                table: "BookDocuments");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "BookDocuments");
        }
    }
}
