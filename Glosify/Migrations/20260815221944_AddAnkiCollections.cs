using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class AddAnkiCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnkiTrackSentencesForward",
                table: "Quizzes");

            migrationBuilder.DropColumn(
                name: "AnkiTrackSentencesReverse",
                table: "Quizzes");

            migrationBuilder.DropColumn(
                name: "AnkiTrackWordsForward",
                table: "Quizzes");

            migrationBuilder.DropColumn(
                name: "AnkiTrackWordsReverse",
                table: "Quizzes");

            migrationBuilder.DropColumn(
                name: "AnkiTrackingEnabled",
                table: "Quizzes");

            migrationBuilder.CreateTable(
                name: "AnkiCollections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SourceLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DefaultDirection = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DesiredRetention = table.Column<double>(type: "float", nullable: false),
                    NewCardsPerDay = table.Column<int>(type: "int", nullable: false),
                    MaximumReviewsPerDay = table.Column<int>(type: "int", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnkiCollections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnkiCollections_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnkiNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnkiCollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    WordId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SentenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SourceText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnkiNotes", x => x.Id);
                    table.CheckConstraint("CK_AnkiNotes_OneSource", "([WordId] IS NOT NULL AND [SentenceId] IS NULL) OR ([WordId] IS NULL AND [SentenceId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AnkiNotes_AnkiCollections_AnkiCollectionId",
                        column: x => x.AnkiCollectionId,
                        principalTable: "AnkiCollections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnkiQuizLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnkiCollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WordsSourceToTarget = table.Column<bool>(type: "bit", nullable: false),
                    WordsTargetToSource = table.Column<bool>(type: "bit", nullable: false),
                    SentencesSourceToTarget = table.Column<bool>(type: "bit", nullable: false),
                    SentencesTargetToSource = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnkiQuizLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnkiQuizLinks_AnkiCollections_AnkiCollectionId",
                        column: x => x.AnkiCollectionId,
                        principalTable: "AnkiCollections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnkiQuizLinks_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AnkiCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnkiNoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Stability = table.Column<double>(type: "float", nullable: false),
                    Difficulty = table.Column<double>(type: "float", nullable: false),
                    LearningStep = table.Column<int>(type: "int", nullable: false),
                    ReviewCount = table.Column<int>(type: "int", nullable: false),
                    LapseCount = table.Column<int>(type: "int", nullable: false),
                    ScheduledDays = table.Column<int>(type: "int", nullable: false),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BuriedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DirectlyIncluded = table.Column<bool>(type: "bit", nullable: false),
                    QuizLinkIncluded = table.Column<bool>(type: "bit", nullable: false),
                    ExcludedFromQuizLink = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnkiCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnkiCards_AnkiNotes_AnkiNoteId",
                        column: x => x.AnkiNoteId,
                        principalTable: "AnkiNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnkiReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnkiCollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnkiCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rating = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PreviousState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NewState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PreviousDueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NewDueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledDays = table.Column<int>(type: "int", nullable: false),
                    ElapsedDays = table.Column<double>(type: "float", nullable: false),
                    PreviousStability = table.Column<double>(type: "float", nullable: false),
                    NewStability = table.Column<double>(type: "float", nullable: false),
                    PreviousDifficulty = table.Column<double>(type: "float", nullable: false),
                    NewDifficulty = table.Column<double>(type: "float", nullable: false),
                    Retrievability = table.Column<double>(type: "float", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Answer = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "int", nullable: true),
                    SchedulerVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnkiReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnkiReviews_AnkiCards_AnkiCardId",
                        column: x => x.AnkiCardId,
                        principalTable: "AnkiCards",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AnkiReviews_AnkiCollections_AnkiCollectionId",
                        column: x => x.AnkiCollectionId,
                        principalTable: "AnkiCollections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnkiCards_AnkiNoteId_Direction",
                table: "AnkiCards",
                columns: new[] { "AnkiNoteId", "Direction" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnkiCards_IsActive_State_DueAt",
                table: "AnkiCards",
                columns: new[] { "IsActive", "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AnkiCollections_UserId_Name",
                table: "AnkiCollections",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnkiCollections_UserId_SourceLanguage_TargetLanguage",
                table: "AnkiCollections",
                columns: new[] { "UserId", "SourceLanguage", "TargetLanguage" });

            migrationBuilder.CreateIndex(
                name: "IX_AnkiNotes_AnkiCollectionId_SentenceId",
                table: "AnkiNotes",
                columns: new[] { "AnkiCollectionId", "SentenceId" },
                unique: true,
                filter: "[SentenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AnkiNotes_AnkiCollectionId_WordId",
                table: "AnkiNotes",
                columns: new[] { "AnkiCollectionId", "WordId" },
                unique: true,
                filter: "[WordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AnkiNotes_QuizId",
                table: "AnkiNotes",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_AnkiQuizLinks_AnkiCollectionId_QuizId",
                table: "AnkiQuizLinks",
                columns: new[] { "AnkiCollectionId", "QuizId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnkiQuizLinks_QuizId",
                table: "AnkiQuizLinks",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_AnkiReviews_AnkiCardId",
                table: "AnkiReviews",
                column: "AnkiCardId");

            migrationBuilder.CreateIndex(
                name: "IX_AnkiReviews_AnkiCollectionId_ReviewedAt",
                table: "AnkiReviews",
                columns: new[] { "AnkiCollectionId", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AnkiReviews_ClientToken",
                table: "AnkiReviews",
                column: "ClientToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnkiQuizLinks");

            migrationBuilder.DropTable(
                name: "AnkiReviews");

            migrationBuilder.DropTable(
                name: "AnkiCards");

            migrationBuilder.DropTable(
                name: "AnkiNotes");

            migrationBuilder.DropTable(
                name: "AnkiCollections");

            migrationBuilder.AddColumn<bool>(
                name: "AnkiTrackSentencesForward",
                table: "Quizzes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnkiTrackSentencesReverse",
                table: "Quizzes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnkiTrackWordsForward",
                table: "Quizzes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnkiTrackWordsReverse",
                table: "Quizzes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnkiTrackingEnabled",
                table: "Quizzes",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
