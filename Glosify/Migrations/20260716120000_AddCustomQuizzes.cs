using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations;

[DbContext(typeof(GlosifyContext))]
[Migration("20260716120000_AddCustomQuizzes")]
public partial class AddCustomQuizzes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Mode",
            table: "QuizAttempts",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(32)",
            oldMaxLength: 32);

        migrationBuilder.CreateTable(
            name: "CustomQuizzes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SchemaVersion = table.Column<int>(type: "int", nullable: false),
                IsPlayable = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomQuizzes", item => item.Id);
                table.ForeignKey(
                    name: "FK_CustomQuizzes_Quizzes_QuizId",
                    column: item => item.QuizId,
                    principalTable: "Quizzes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CustomQuizzes_QuizId_IsPlayable",
            table: "CustomQuizzes",
            columns: new[] { "QuizId", "IsPlayable" });

        migrationBuilder.CreateIndex(
            name: "IX_CustomQuizzes_QuizId_Name",
            table: "CustomQuizzes",
            columns: new[] { "QuizId", "Name" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CustomQuizzes");
        migrationBuilder.AlterColumn<string>(
            name: "Mode",
            table: "QuizAttempts",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(200)",
            oldMaxLength: 200);
    }
}
