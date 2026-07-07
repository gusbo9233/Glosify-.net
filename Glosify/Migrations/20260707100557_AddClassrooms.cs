using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddClassrooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    table.ForeignKey(
                        name: "FK_AcsUserIdentities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Classrooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    JoinCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    JoinCodeEnabled = table.Column<bool>(type: "bit", nullable: false),
                    GroupCallId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classrooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Classrooms_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomContents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<int>(type: "int", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SharedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SharedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomContents_AspNetUsers_SharedByUserId",
                        column: x => x.SharedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomContents_BookDocuments_BookDocumentId",
                        column: x => x.BookDocumentId,
                        principalTable: "BookDocuments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomContents_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomContents_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ClassroomInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InvitedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomInvitations_Classrooms_ClassroomId",
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
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastChatReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
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
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomMessages_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomMessages_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuizAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PracticeDirection = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PracticeItemType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TotalItems = table.Column<int>(type: "int", nullable: false),
                    CorrectCount = table.Column<int>(type: "int", nullable: false),
                    IncorrectCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizAttempts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuizAttempts_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuizAttempts_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuizAttemptItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ExpectedAnswer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    GivenAnswer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    IsCorrect = table.Column<bool>(type: "bit", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizAttemptItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizAttemptItems_QuizAttempts_QuizAttemptId",
                        column: x => x.QuizAttemptId,
                        principalTable: "QuizAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttemptItems_QuizAttemptId_Sequence",
                table: "QuizAttemptItems",
                columns: new[] { "QuizAttemptId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_ClassroomId_QuizId_CompletedAt",
                table: "QuizAttempts",
                columns: new[] { "ClassroomId", "QuizId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_QuizId",
                table: "QuizAttempts",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_UserId_CompletedAt",
                table: "QuizAttempts",
                columns: new[] { "UserId", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcsUserIdentities");

            migrationBuilder.DropTable(
                name: "ClassroomContents");

            migrationBuilder.DropTable(
                name: "ClassroomInvitations");

            migrationBuilder.DropTable(
                name: "ClassroomMemberships");

            migrationBuilder.DropTable(
                name: "ClassroomMessages");

            migrationBuilder.DropTable(
                name: "QuizAttemptItems");

            migrationBuilder.DropTable(
                name: "QuizAttempts");

            migrationBuilder.DropTable(
                name: "Classrooms");
        }
    }
}
