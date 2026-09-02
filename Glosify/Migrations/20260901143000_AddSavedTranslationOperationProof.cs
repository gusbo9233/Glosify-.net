using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations;

[DbContext(typeof(GlosifyContext))]
[Migration("20260901143000_AddSavedTranslationOperationProof")]
public sealed class AddSavedTranslationOperationProof : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "TranslationOperationId",
            table: "SavedTranslations",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SavedTranslations_UserId_TranslationOperationId",
            table: "SavedTranslations",
            columns: ["UserId", "TranslationOperationId"],
            unique: true,
            filter: "[TranslationOperationId] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SavedTranslations_UserId_TranslationOperationId",
            table: "SavedTranslations");

        migrationBuilder.DropColumn(
            name: "TranslationOperationId",
            table: "SavedTranslations");
    }
}
