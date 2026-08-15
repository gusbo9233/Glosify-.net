using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayCulture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayCulture",
                table: "AspNetUsers",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_DisplayCulture",
                table: "AspNetUsers",
                sql: "[DisplayCulture] IS NULL OR [DisplayCulture] IN ('en-GB', 'sv-SE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_DisplayCulture",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DisplayCulture",
                table: "AspNetUsers");
        }
    }
}
