using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddFreestyleQuizMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_SelectedQuizLanguageCode",
                table: "AspNetUsers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_SelectedQuizLanguageCode",
                table: "AspNetUsers",
                sql: "[SelectedQuizLanguageCode] IS NULL OR [SelectedQuizLanguageCode] IN ('af', 'ar', 'hy', 'as', 'az', 'bn', 'bs', 'bg', 'my', 'yue', 'ca', 'zh-Hans', 'hr', 'cs', 'da', 'nl', 'en', 'et', 'fil', 'fi', 'fr', 'gl', 'ka', 'de', 'el', 'gu', 'ha', 'he', 'hi', 'hu', 'is', 'id', 'it', 'ja', 'kn', 'kk', 'ko', 'ky', 'lv', 'lt', 'mk', 'ms', 'ml', 'mt', 'mi', 'mr', 'ne', 'nb', 'or', 'fa', 'pl', 'pt', 'pa', 'ro', 'ru', 'sr-Latn', 'sk', 'sl', 'es', 'sw', 'sv', 'ta', 'te', 'th', 'tr', 'uk', 'uz', 'vi', 'cy', 'free')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_SelectedQuizLanguageCode",
                table: "AspNetUsers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_SelectedQuizLanguageCode",
                table: "AspNetUsers",
                sql: "[SelectedQuizLanguageCode] IS NULL OR [SelectedQuizLanguageCode] IN ('af', 'ar', 'hy', 'as', 'az', 'bn', 'bs', 'bg', 'my', 'yue', 'ca', 'zh-Hans', 'hr', 'cs', 'da', 'nl', 'en', 'et', 'fil', 'fi', 'fr', 'gl', 'ka', 'de', 'el', 'gu', 'ha', 'he', 'hi', 'hu', 'is', 'id', 'it', 'ja', 'kn', 'kk', 'ko', 'ky', 'lv', 'lt', 'mk', 'ms', 'ml', 'mt', 'mi', 'mr', 'ne', 'nb', 'or', 'fa', 'pl', 'pt', 'pa', 'ro', 'ru', 'sr-Latn', 'sk', 'sl', 'es', 'sw', 'sv', 'ta', 'te', 'th', 'tr', 'uk', 'uz', 'vi', 'cy')");
        }
    }
}
