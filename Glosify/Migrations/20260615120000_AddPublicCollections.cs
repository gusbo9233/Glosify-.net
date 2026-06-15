using System;
using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(GlosifyContext))]
    [Migration("20260615120000_AddPublicCollections")]
    public partial class AddPublicCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "Collections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalCollectionId",
                table: "Collections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Collections_IsPublic_Language",
                table: "Collections",
                columns: new[] { "IsPublic", "Language" });

            migrationBuilder.CreateIndex(
                name: "IX_Collections_OriginalCollectionId",
                table: "Collections",
                column: "OriginalCollectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Collections_IsPublic_Language",
                table: "Collections");

            migrationBuilder.DropIndex(
                name: "IX_Collections_OriginalCollectionId",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "OriginalCollectionId",
                table: "Collections");
        }
    }
}
