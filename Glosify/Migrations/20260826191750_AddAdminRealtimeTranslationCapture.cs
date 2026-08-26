using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminRealtimeTranslationCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RealtimeTranslationCaptureEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TargetLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ProviderRequest = table.Column<bool>(type: "bit", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StoredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationCaptureEvents", x => x.Id);
                    table.CheckConstraint("CK_RealtimeTranslationCaptureEvents_Kind", "[Kind] IN ('partial', 'final')");
                    table.CheckConstraint("CK_RealtimeTranslationCaptureEvents_Stage", "[Stage] IN ('scribe', 'translator', 'bubble')");
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationCaptureEvents_RealtimeTranslationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "RealtimeTranslationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationCaptureEvents_SessionId_Ordinal",
                table: "RealtimeTranslationCaptureEvents",
                columns: new[] { "SessionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationCaptureEvents_SessionId_Stage_CapturedAt",
                table: "RealtimeTranslationCaptureEvents",
                columns: new[] { "SessionId", "Stage", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RealtimeTranslationCaptureEvents");
        }
    }
}
