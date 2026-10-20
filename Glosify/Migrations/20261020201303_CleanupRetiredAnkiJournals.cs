using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class CleanupRetiredAnkiJournals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SimplifyAnki already removed the collections referenced by these legacy
            // tool journals, but only cleaned the newer anki_* journal kinds. Repair
            // databases that have applied it without touching subsequent study data.
            // Delete ledger snapshots first, while their journal keys still exist.
            migrationBuilder.Sql("""
                DELETE FROM [ResourceEntry]
                WHERE [EntityType] = N'AssistantChange' AND JSON_VALUE([EntityKeyJson], '$[0]') IN (
                    SELECT LOWER(CONVERT(nvarchar(36), [id])) FROM [assistant_changes]
                    WHERE [kind] IN (N'create_anki_collection', N'rename_anki_collection'));
                DELETE FROM [assistant_changes]
                WHERE [kind] IN (N'create_anki_collection', N'rename_anki_collection');
                """);

            // Deployment stops workers before applying migrations. Rebuild counters
            // from the retained ledger in this transaction, preserving the Ready gate.
            migrationBuilder.Sql("""
                DELETE FROM [ResourceUsage];
                WITH charges AS (
                    SELECT e.[UserId], j.[key] AS [Resource], CONVERT(bigint, j.[value]) AS [Amount]
                    FROM [ResourceEntry] e CROSS APPLY OPENJSON(e.[ChargesJson]) j
                ), scoped AS (
                    SELECT [UserId] AS [Scope], [Resource], [Amount] FROM charges
                    UNION ALL
                    SELECT N'$site', [Resource], [Amount] FROM charges
                    WHERE [Resource] IN (N'content_bytes', N'pdf_bytes')
                )
                INSERT INTO [ResourceUsage] ([Scope], [Resource], [Used])
                SELECT [Scope], [Resource], SUM([Amount]) FROM scoped
                GROUP BY [Scope], [Resource];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted journals referenced collections removed by SimplifyAnki and
            // cannot be restored. The repair makes no schema changes to roll back.
        }
    }
}
