using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddClassroomLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Classrooms",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // Classrooms created before the list was language-scoped are all Polish.
            migrationBuilder.Sql(
                """
                UPDATE [Classrooms]
                SET [Language] = N'Polish'
                WHERE [Language] IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "Classrooms");
        }
    }
}
