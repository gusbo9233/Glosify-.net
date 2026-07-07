using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddCoursePlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassroomLessons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomLessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomLessons_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomLessons_Classrooms_ClassroomId",
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
                    LessonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
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
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

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
                name: "IX_ClassroomLessons_ClassroomId_ScheduledAt",
                table: "ClassroomLessons",
                columns: new[] { "ClassroomId", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomLessons_CreatedByUserId",
                table: "ClassroomLessons",
                column: "CreatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassroomAssignments");

            migrationBuilder.DropTable(
                name: "ClassroomLessons");
        }
    }
}
