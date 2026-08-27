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
        Assert.DoesNotContain(operations, operation => operation is InsertDataOperation);
    }
}
