using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class AssistantToolsTests
{
    [Fact]
    public async Task CreateQuiz_QueuesPendingChangeWithCurrentLanguageDefault()
    {
        var tools = new AssistantTools(null!);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Spanish"
        };

        var result = await tools.ExecuteAsync(
            "create_quiz",
            """{"name":"Travel Basics","source_language":"English"}""",
            context,
            CancellationToken.None);

        Assert.Single(context.PendingChanges);
        Assert.Equal(PendingChangeKinds.CreateQuiz, context.PendingChanges[0].Kind);
        Assert.Contains("queued", JsonSerializer.Serialize(result));

        var payload = context.PendingChanges[0].Payload;
        Assert.Equal("Travel Basics", payload.GetProperty("name").GetString());
        Assert.Equal("English", payload.GetProperty("source_language").GetString());
        Assert.Equal("Spanish", payload.GetProperty("target_language").GetString());
    }

    [Fact]
    public async Task CreateCollection_QueuesPendingChangeWithCurrentLanguageDefault()
    {
        var tools = new AssistantTools(null!);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "French"
        };

        await tools.ExecuteAsync(
            "create_collection",
            """{"name":"Food"}""",
            context,
            CancellationToken.None);

        Assert.Single(context.PendingChanges);
        Assert.Equal(PendingChangeKinds.CreateCollection, context.PendingChanges[0].Kind);

        var payload = context.PendingChanges[0].Payload;
        Assert.Equal("Food", payload.GetProperty("name").GetString());
        Assert.Equal("French", payload.GetProperty("language").GetString());
    }

    [Fact]
    public async Task CreateCollection_RejectsInvalidParentCollectionId()
    {
        var tools = new AssistantTools(null!);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "French"
        };

        var result = await tools.ExecuteAsync(
            "create_collection",
            """{"name":"Food","parent_collection_id":"not-a-guid"}""",
            context,
            CancellationToken.None);

        Assert.Empty(context.PendingChanges);
        Assert.Contains("parent_collection_id must be a valid id", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AddWord_RequiresQuizContext()
    {
        var tools = new AssistantTools(null!);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Spanish"
        };

        var result = await tools.ExecuteAsync(
            "add_word",
            """{"word":"casa","translation":"house"}""",
            context,
            CancellationToken.None);

        Assert.Empty(context.PendingChanges);
        Assert.Contains("Choose a quiz", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AddWords_QueuesOnePendingChangePerWord()
    {
        var tools = new AssistantTools(null!);
        var context = new AgentToolContext
        {
            QuizId = Guid.NewGuid(),
            UserId = "user-1",
            CurrentLanguage = "Polish"
        };

        var result = await tools.ExecuteAsync(
            "add_words",
            """
            {
              "words": [
                { "word": "iść", "translation": "to go" },
                { "word": "robić", "translation": "to do" }
              ]
            }
            """,
            context,
            CancellationToken.None);

        Assert.Equal(2, context.PendingChanges.Count);
        Assert.All(context.PendingChanges, change => Assert.Equal(PendingChangeKinds.AddWord, change.Kind));
        Assert.Contains("\"count\":2", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task EditWords_QueuesOnePendingChangePerEdit()
    {
        var tools = new AssistantTools(null!);
        var context = new AgentToolContext
        {
            QuizId = Guid.NewGuid(),
            UserId = "user-1",
            CurrentLanguage = "Polish"
        };

        var result = await tools.ExecuteAsync(
            "edit_words",
            """
            {
              "changes": [
                { "word_id": "word-1", "word": "idę" },
                { "word_id": "word-2", "word": "robię", "translation": "I do" }
              ]
            }
            """,
            context,
            CancellationToken.None);

        Assert.Equal(2, context.PendingChanges.Count);
        Assert.All(context.PendingChanges, change => Assert.Equal(PendingChangeKinds.EditWord, change.Kind));
        Assert.Equal("word-1", context.PendingChanges[0].Payload.GetProperty("word_id").GetString());
        Assert.Equal("idę", context.PendingChanges[0].Payload.GetProperty("word").GetString());
        Assert.Contains("\"count\":2", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task EditWords_IncludesOriginalWordValuesWhenAvailable()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(new Quiz
        {
            Id = quizId,
            UserId = "user-1",
            Name = "Polish verbs",
            SourceLanguage = "English",
            TargetLanguage = "Polish",
            Language = "Polish",
        });
        db.Words.Add(new Word
        {
            Id = "word-1",
            QuizId = quizId,
            Lemma = "robić",
            Translation = "to do / to make",
        });
        await db.SaveChangesAsync();

        var tools = new AssistantTools(db);
        var context = new AgentToolContext
        {
            QuizId = quizId,
            UserId = "user-1",
            CurrentLanguage = "Polish"
        };

        await tools.ExecuteAsync(
            "edit_words",
            """{"changes":[{"word_id":"word-1","word":"robię","translation":"I do / I make"}]}""",
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal("robić", payload.GetProperty("original_word").GetString());
        Assert.Equal("to do / to make", payload.GetProperty("original_translation").GetString());
        Assert.Equal("robię", payload.GetProperty("word").GetString());
        Assert.Equal("I do / I make", payload.GetProperty("translation").GetString());
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }
}
