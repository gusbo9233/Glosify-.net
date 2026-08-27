using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSpeakingAndClassrooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuizAttempts_Classrooms_ClassroomId",
                table: "QuizAttempts");

            migrationBuilder.DropTable(
                name: "AcsUserIdentities");

            migrationBuilder.DropTable(
                name: "ClassroomAssignments");

            migrationBuilder.DropTable(
                name: "ClassroomContents");

            migrationBuilder.DropTable(
                name: "ClassroomInvitations");

            migrationBuilder.DropTable(
                name: "ClassroomMemberships");

            migrationBuilder.DropTable(
                name: "ClassroomMessages");

            migrationBuilder.DropTable(
                name: "ClassroomLessons");

            migrationBuilder.DropTable(
                name: "Classrooms");

            migrationBuilder.DropIndex(
                name: "IX_QuizAttempts_ClassroomId_QuizId_CompletedAt",
                table: "QuizAttempts");

            migrationBuilder.DropColumn(
                name: "ClassroomId",
                table: "QuizAttempts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClassroomId",
                table: "QuizAttempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AcsUserIdentities",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AcsUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcsUserIdentities", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Classrooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    GroupCallId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    JoinCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    JoinCodeEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classrooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomContents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SharedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SharedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomContents_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InvitedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomInvitations_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomLessons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomLessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomLessons_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastChatReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomMemberships_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomMessages_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LessonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_ClassroomLessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "ClassroomLessons",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_ClassroomId_QuizId_CompletedAt",
                table: "QuizAttempts",
                columns: new[] { "ClassroomId", "QuizId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_ClassroomId_DueAt",
                table: "ClassroomAssignments",
                columns: new[] { "ClassroomId", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_CreatedByUserId",
                table: "ClassroomAssignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_LessonId",
                table: "ClassroomAssignments",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_QuizId",
                table: "ClassroomAssignments",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_BookDocumentId",
                table: "ClassroomContents",
                column: "BookDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_ClassroomId_BookDocumentId",
                table: "ClassroomContents",
                columns: new[] { "ClassroomId", "BookDocumentId" },
                unique: true,
                filter: "[BookDocumentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_ClassroomId_QuizId",
                table: "ClassroomContents",
                columns: new[] { "ClassroomId", "QuizId" },
                unique: true,
                filter: "[QuizId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_ClassroomId_SharedAt",
                table: "ClassroomContents",
                columns: new[] { "ClassroomId", "SharedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_QuizId",
                table: "ClassroomContents",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_SharedByUserId",
                table: "ClassroomContents",
                column: "SharedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomInvitations_ClassroomId_Email",
                table: "ClassroomInvitations",
                columns: new[] { "ClassroomId", "Email" },
                unique: true,
                filter: "[AcceptedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomInvitations_Email",
                table: "ClassroomInvitations",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomInvitations_InvitedByUserId",
                table: "ClassroomInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomLessons_ClassroomId_ScheduledAt",
                table: "ClassroomLessons",
                columns: new[] { "ClassroomId", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomLessons_CreatedByUserId",
                table: "ClassroomLessons",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMemberships_ClassroomId_UserId",
                table: "ClassroomMemberships",
                columns: new[] { "ClassroomId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMemberships_UserId",
                table: "ClassroomMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMessages_ClassroomId_Kind_CreatedAt",
                table: "ClassroomMessages",
                columns: new[] { "ClassroomId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMessages_UserId",
                table: "ClassroomMessages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_JoinCode",
                table: "Classrooms",
                column: "JoinCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_OwnerUserId",
                table: "Classrooms",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_QuizAttempts_Classrooms_ClassroomId",
                table: "QuizAttempts",
                column: "ClassroomId",
                principalTable: "Classrooms",
                principalColumn: "Id");
        }
    }
}
