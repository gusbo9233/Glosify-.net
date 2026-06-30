using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    [DbContext(typeof(GlosifyContext))]
    [Migration("20260630120000_AddWordCreatedAtForInsertionOrder")]
    public partial class AddWordCreatedAtForInsertionOrder : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "words",
                type: "datetimeoffset",
                nullable: true);

            // The old schema did not record insertion time. Keep the prior
            // alphabetical order stable for existing rows, while all newly
            // created rows receive their real creation timestamp.
            migrationBuilder.Sql(
                """
                WITH [ordered_words] AS
                (
                    SELECT
                        [id],
                        ROW_NUMBER() OVER (
                            PARTITION BY [quiz_id]
                            ORDER BY [lemma], [id]) AS [row_number]
                    FROM [words]
                )
                UPDATE [word]
                SET [created_at] = DATEADD(
                    millisecond,
                    [ordered].[row_number],
                    CAST('2000-01-01T00:00:00+00:00' AS datetimeoffset))
                FROM [words] AS [word]
                INNER JOIN [ordered_words] AS [ordered]
                    ON [ordered].[id] = [word].[id];
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "created_at",
                table: "words",
                type: "datetimeoffset",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset",
                oldNullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_at",
                table: "words");
        }
    }
}
