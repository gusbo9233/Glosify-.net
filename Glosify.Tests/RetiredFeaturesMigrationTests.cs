using Glosify.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Glosify.Tests;

public sealed class RetiredFeaturesMigrationTests
{
    private static readonly HashSet<string> RetiredTables =
    [
        "AcsUserIdentities",
        "ClassroomAssignments",
        "ClassroomContents",
        "ClassroomInvitations",
        "ClassroomLessons",
        "ClassroomMemberships",
        "ClassroomMessages",
        "Classrooms",
    ];

    private static readonly HashSet<string> DetachedRetainedReferenceForeignKeys =
    [
        "FK_AcsUserIdentities_AspNetUsers_UserId",
        "FK_Classrooms_AspNetUsers_OwnerUserId",
        "FK_ClassroomAssignments_AspNetUsers_CreatedByUserId",
        "FK_ClassroomAssignments_Quizzes_QuizId",
        "FK_ClassroomContents_AspNetUsers_SharedByUserId",
        "FK_ClassroomContents_BookDocuments_BookDocumentId",
        "FK_ClassroomContents_Quizzes_QuizId",
        "FK_ClassroomInvitations_AspNetUsers_InvitedByUserId",
        "FK_ClassroomLessons_AspNetUsers_CreatedByUserId",
        "FK_ClassroomMemberships_AspNetUsers_UserId",
        "FK_ClassroomMessages_AspNetUsers_UserId",
    ];

    [Fact]
    public void CompatibilityUpDetachesOnlyRetiredReferencesIntoRetainedTables()
    {
        var operations = new PrepareClassroomRetirement().UpOperations;

        var droppedForeignKeys = operations.OfType<DropForeignKeyOperation>()
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(DetachedRetainedReferenceForeignKeys.SetEquals(droppedForeignKeys));
        Assert.All(operations, operation => Assert.IsType<DropForeignKeyOperation>(operation));
    }

    [Fact]
    public void CompatibilityDownRestoresTheDetachedReferences()
    {
        var operations = new PrepareClassroomRetirement().DownOperations;

        var restoredForeignKeys = operations.OfType<AddForeignKeyOperation>()
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(DetachedRetainedReferenceForeignKeys.SetEquals(restoredForeignKeys));
        Assert.All(operations, operation => Assert.IsType<AddForeignKeyOperation>(operation));
    }

    [Fact]
    public void UpDropsOnlyRetiredTablesAndTheClassroomAttemptAssociation()
    {
        var operations = new RemoveSpeakingAndClassrooms().UpOperations;

        var droppedTables = operations.OfType<DropTableOperation>()
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(RetiredTables.SetEquals(droppedTables));

        var droppedColumn = Assert.Single(operations.OfType<DropColumnOperation>());
        Assert.Equal("QuizAttempts", droppedColumn.Table);
        Assert.Equal("ClassroomId", droppedColumn.Name);

        Assert.DoesNotContain(operations.OfType<DropTableOperation>(), operation =>
            operation.Name is "QuizAttempts" or "QuizAttemptItems");
        Assert.DoesNotContain(operations, operation => operation is DeleteDataOperation);
    }

    [Fact]
    public void DownRecreatesEmptyRetiredSchemaWithoutRestoringRows()
    {
        var operations = new RemoveSpeakingAndClassrooms().DownOperations;

        var recreatedTables = operations.OfType<CreateTableOperation>()
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(RetiredTables.SetEquals(recreatedTables));

        var restoredColumn = Assert.Single(operations.OfType<AddColumnOperation>());
        Assert.Equal("QuizAttempts", restoredColumn.Table);
        Assert.Equal("ClassroomId", restoredColumn.Name);
        var recreatedForeignKeys = operations.OfType<CreateTableOperation>()
            .SelectMany(operation => operation.ForeignKeys)
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(recreatedForeignKeys.Intersect(DetachedRetainedReferenceForeignKeys));
        Assert.DoesNotContain(operations, operation => operation is InsertDataOperation);
    }

    [Fact]
    public void CustomQuizRetirementRejectsStaleProposalsAndDropsOnlyCustomQuizzes()
    {
        var operations = new RemoveCustomQuizzes().UpOperations;
        var sql = Assert.Single(operations.OfType<SqlOperation>()).Sql;
        var droppedTable = Assert.Single(operations.OfType<DropTableOperation>());

        Assert.Equal("CustomQuizzes", droppedTable.Name);
        Assert.Contains("assistant_messages", sql, StringComparison.Ordinal);
        Assert.Contains("assistant_pending_changes", sql, StringComparison.Ordinal);
        Assert.Contains("create_custom_quiz", sql, StringComparison.Ordinal);
        Assert.Contains("JSON_QUERY(change.payload, '$.custom_quiz')", sql, StringComparison.Ordinal);
        Assert.Contains("status = N'rejected'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(operations, operation => operation is DeleteDataOperation);
    }

    [Fact]
    public void CustomQuizRetirementDownRecreatesOnlyAnEmptyCustomQuizSchema()
    {
        var operations = new RemoveCustomQuizzes().DownOperations;
        var table = Assert.Single(operations.OfType<CreateTableOperation>());

        Assert.Equal("CustomQuizzes", table.Name);
        Assert.Equal("FK_CustomQuizzes_Quizzes_QuizId", Assert.Single(table.ForeignKeys).Name);
        Assert.Equal(2, operations.OfType<CreateIndexOperation>().Count());
        Assert.DoesNotContain(operations, operation => operation is InsertDataOperation);
    }
}
