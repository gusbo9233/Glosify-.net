using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCustomQuizzes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DECLARE @RetiredMessages TABLE
                (
                    id uniqueidentifier NOT NULL PRIMARY KEY,
                    turn_id uniqueidentifier NULL
                );

                INSERT INTO @RetiredMessages (id, turn_id)
                SELECT message.id, message.turn_id
                FROM assistant_messages AS message
                WHERE message.status = N'active'
                  AND ISJSON(message.pending_changes_json) = 1
                  AND EXISTS
                  (
                      SELECT 1
                      FROM OPENJSON(message.pending_changes_json)
                      WITH
                      (
                          kind nvarchar(64) '$.kind',
                          payload nvarchar(max) '$.payload' AS JSON
                      ) AS change
                      WHERE change.kind IN
                      (
                          N'create_custom_quiz',
                          N'add_custom_quiz_element',
                          N'add_custom_quiz_elements',
                          N'configure_custom_quiz_element',
                          N'remove_custom_quiz_element'
                      )
                      OR
                      (
                          change.kind = N'create_quiz'
                          AND JSON_QUERY(change.payload, '$.custom_quiz') IS NOT NULL
                      )
                  );

                UPDATE turn_row
                SET change_outcome = N'rejected',
                    change_outcome_at = SYSDATETIMEOFFSET()
                FROM assistant_turns AS turn_row
                INNER JOIN @RetiredMessages AS retired ON retired.turn_id = turn_row.id;

                UPDATE message
                SET status = N'rejected'
                FROM assistant_messages AS message
                INNER JOIN @RetiredMessages AS retired ON retired.id = message.id;

                UPDATE assistant_pending_changes
                SET status = N'rejected'
                WHERE status = N'pending'
                  AND
                  (
                      kind IN
                      (
                          N'create_custom_quiz',
                          N'add_custom_quiz_element',
                          N'add_custom_quiz_elements',
                          N'configure_custom_quiz_element',
                          N'remove_custom_quiz_element'
                      )
                      OR
                      (
                          kind = N'create_quiz'
                          AND ISJSON(payload_json) = 1
                          AND JSON_QUERY(payload_json, '$.custom_quiz') IS NOT NULL
                      )
                  );
                """);

            migrationBuilder.DropTable(
                name: "CustomQuizzes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomQuizzes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsPlayable = table.Column<bool>(type: "bit", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomQuizzes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomQuizzes_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomQuizzes_QuizId_IsPlayable",
                table: "CustomQuizzes",
                columns: new[] { "QuizId", "IsPlayable" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomQuizzes_QuizId_Name",
                table: "CustomQuizzes",
                columns: new[] { "QuizId", "Name" },
                unique: true);
        }
    }
}
