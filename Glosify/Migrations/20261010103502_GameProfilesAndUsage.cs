using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class GameProfilesAndUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GamePlayerProfile",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CharacterJson = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamePlayerProfile", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_GamePlayerProfile_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GamePlaySession",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActiveSeconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamePlaySession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameUsageEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ServiceTier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Measurement = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CachedInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    AudioSeconds = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    Characters = table.Column<long>(type: "bigint", nullable: true),
                    EstimatedUsd = table.Column<decimal>(type: "decimal(20,10)", precision: 20, scale: 10, nullable: true),
                    RateJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameUsageEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GamePlaySession_StartedAt_UserId",
                table: "GamePlaySession",
                columns: new[] { "StartedAt", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GameUsageEvent_SessionId",
                table: "GameUsageEvent",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GameUsageEvent_StartedAt_UserId",
                table: "GameUsageEvent",
                columns: new[] { "StartedAt", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GamePlayerProfile");

            migrationBuilder.DropTable(
                name: "GamePlaySession");

            migrationBuilder.DropTable(
                name: "GameUsageEvent");
        }
    }
}
