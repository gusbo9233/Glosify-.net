using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class DurableAbuseControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StorageReservationId",
                table: "RealtimeTranslationSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TranscriptStorageStopped",
                table: "RealtimeTranslationSessions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FileSizeBytes",
                table: "BookDocuments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "BlobCleanupRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    BlobName = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobCleanupRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourceAccountingState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Ready = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceAccountingState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourceEntry",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityKeyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CascadeAncestors = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChargesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceEntry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourceReservation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ChargesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BlobName = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceReservation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourceUsage",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Resource = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Used = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceUsage", x => new { x.Scope, x.Resource });
                });

            migrationBuilder.CreateTable(
                name: "SignupBucket",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignupBucket", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpeechBudgetReservation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodKey = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    AmountMicros = table.Column<long>(type: "bigint", nullable: false),
                    ActualMicros = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Settled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeechBudgetReservation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlobCleanupRequest_BlobName",
                table: "BlobCleanupRequest",
                column: "BlobName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceEntry_UserId",
                table: "ResourceEntry",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceReservation_ExpiresAt",
                table: "ResourceReservation",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceReservation_UserId",
                table: "ResourceReservation",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SignupBucket_ExpiresAt",
                table: "SignupBucket",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SpeechBudgetReservation_ExpiresAt",
                table: "SpeechBudgetReservation",
                column: "ExpiresAt",
                filter: "[Settled] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlobCleanupRequest");

            migrationBuilder.DropTable(
                name: "ResourceAccountingState");

            migrationBuilder.DropTable(
                name: "ResourceEntry");

            migrationBuilder.DropTable(
                name: "ResourceReservation");

            migrationBuilder.DropTable(
                name: "ResourceUsage");

            migrationBuilder.DropTable(
                name: "SignupBucket");

            migrationBuilder.DropTable(
                name: "SpeechBudgetReservation");

            migrationBuilder.DropColumn(
                name: "StorageReservationId",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "TranscriptStorageStopped",
                table: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "FileSizeBytes",
                table: "BookDocuments");
        }
    }
}
