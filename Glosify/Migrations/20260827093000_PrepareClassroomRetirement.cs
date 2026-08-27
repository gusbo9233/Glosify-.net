using Glosify.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations;

[DbContext(typeof(GlosifyContext))]
[Migration("20260827093000_PrepareClassroomRetirement")]
public partial class PrepareClassroomRetirement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_AcsUserIdentities_AspNetUsers_UserId",
            table: "AcsUserIdentities");

        migrationBuilder.DropForeignKey(
            name: "FK_Classrooms_AspNetUsers_OwnerUserId",
            table: "Classrooms");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomAssignments_AspNetUsers_CreatedByUserId",
            table: "ClassroomAssignments");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomAssignments_Quizzes_QuizId",
            table: "ClassroomAssignments");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomContents_AspNetUsers_SharedByUserId",
            table: "ClassroomContents");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomContents_BookDocuments_BookDocumentId",
            table: "ClassroomContents");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomContents_Quizzes_QuizId",
            table: "ClassroomContents");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomInvitations_AspNetUsers_InvitedByUserId",
            table: "ClassroomInvitations");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomLessons_AspNetUsers_CreatedByUserId",
            table: "ClassroomLessons");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomMemberships_AspNetUsers_UserId",
            table: "ClassroomMemberships");

        migrationBuilder.DropForeignKey(
            name: "FK_ClassroomMessages_AspNetUsers_UserId",
            table: "ClassroomMessages");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddForeignKey(
            name: "FK_AcsUserIdentities_AspNetUsers_UserId",
            table: "AcsUserIdentities",
            column: "UserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_Classrooms_AspNetUsers_OwnerUserId",
            table: "Classrooms",
            column: "OwnerUserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomAssignments_AspNetUsers_CreatedByUserId",
            table: "ClassroomAssignments",
            column: "CreatedByUserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomAssignments_Quizzes_QuizId",
            table: "ClassroomAssignments",
            column: "QuizId",
            principalTable: "Quizzes",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomContents_AspNetUsers_SharedByUserId",
            table: "ClassroomContents",
            column: "SharedByUserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomContents_BookDocuments_BookDocumentId",
            table: "ClassroomContents",
            column: "BookDocumentId",
            principalTable: "BookDocuments",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomContents_Quizzes_QuizId",
            table: "ClassroomContents",
            column: "QuizId",
            principalTable: "Quizzes",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomInvitations_AspNetUsers_InvitedByUserId",
            table: "ClassroomInvitations",
            column: "InvitedByUserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomLessons_AspNetUsers_CreatedByUserId",
            table: "ClassroomLessons",
            column: "CreatedByUserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomMemberships_AspNetUsers_UserId",
            table: "ClassroomMemberships",
            column: "UserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_ClassroomMessages_AspNetUsers_UserId",
            table: "ClassroomMessages",
            column: "UserId",
            principalTable: "AspNetUsers",
            principalColumn: "Id");
    }
}
