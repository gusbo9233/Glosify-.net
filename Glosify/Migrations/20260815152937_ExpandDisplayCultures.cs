using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class ExpandDisplayCultures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_DisplayCulture",
                table: "AspNetUsers");

            migrationBuilder.Sql(
                "UPDATE [AspNetUsers] SET [DisplayCulture] = 'en-GB' " +
                "WHERE [DisplayCulture] IS NOT NULL AND [DisplayCulture] NOT IN ('en-GB', 'sv-SE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_DisplayCulture",
                table: "AspNetUsers",
                sql: "[DisplayCulture] IS NULL OR [DisplayCulture] IN ('en-GB', 'sv-SE', 'es-419', 'pt-BR', 'fr-FR', 'ja-JP', 'zh-Hans', 'uk-UA', 'tr-TR', 'id-ID', 'vi-VN', 'ar')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_DisplayCulture",
                table: "AspNetUsers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_DisplayCulture",
                table: "AspNetUsers",
                sql: "[DisplayCulture] IS NULL OR [DisplayCulture] IN ('en-GB', 'sv-SE')");
        }
    }
}
