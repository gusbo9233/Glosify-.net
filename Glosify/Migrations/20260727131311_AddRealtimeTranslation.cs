using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeTranslation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudioDurationSeconds",
                table: "AiCreditTransactions",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChargedMinutes = table.Column<int>(type: "int", nullable: false),
                    CreditsCharged = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationMinutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinuteIndex = table.Column<int>(type: "int", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BegunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationMinutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationMinutes_RealtimeTranslationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "RealtimeTranslationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationMinutes_ReservationId",
                table: "RealtimeTranslationMinutes",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationMinutes_SessionId_MinuteIndex",
                table: "RealtimeTranslationMinutes",
                columns: new[] { "SessionId", "MinuteIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationSessions_UserId",
                table: "RealtimeTranslationSessions",
                column: "UserId",
                unique: true,
                filter: "[Status] IN ('pending', 'active')");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationSessions_UserId_CreatedAt",
                table: "RealtimeTranslationSessions",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RealtimeTranslationMinutes");

            migrationBuilder.DropTable(
                name: "RealtimeTranslationSessions");

            migrationBuilder.DropColumn(
                name: "AudioDurationSeconds",
                table: "AiCreditTransactions");
        }
    }
}
