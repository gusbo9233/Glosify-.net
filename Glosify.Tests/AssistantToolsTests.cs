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
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
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
    public async Task CreateQuiz_FallsBackToCurrentLanguageWhenTargetLanguageIsBlank()
    {
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Spanish"
        };

        await tools.ExecuteAsync(
            "create_quiz",
            """{"name":"Travel Basics","source_language":"English","target_language":""}""",
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal("Spanish", payload.GetProperty("target_language").GetString());
    }

    [Fact]
    public async Task CreateCollection_QueuesPendingChangeWithCurrentLanguageDefault()
    {
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
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
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
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
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
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
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
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
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
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

    [Fact]
    public async Task AddWords_ReportsSkippedItemsWithReasons()
    {
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
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
                { "word": "robić" },
                { "translation": "to have" }
              ]
            }
            """,
            context,
            CancellationToken.None);

        Assert.Single(context.PendingChanges);
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("\"Index\":1", json);
        Assert.Contains("\"Index\":2", json);
    }

    [Fact]
    public async Task ListWords_PagesResultsAndReportsTotalCount()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Words.AddRange(
            new Word { Id = "w1", QuizId = quizId, Lemma = "a", Translation = "1" },
            new Word { Id = "w2", QuizId = quizId, Lemma = "b", Translation = "2" },
            new Word { Id = "w3", QuizId = quizId, Lemma = "c", Translation = "3" });
        await db.SaveChangesAsync();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { QuizId = quizId, UserId = "user-1" };

        var result = await tools.ExecuteAsync("list_words", """{"offset":1}""", context, CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"total_count\":3", json);
        Assert.Contains("\"offset\":1", json);
        Assert.Contains("\"has_more\":false", json);
        Assert.DoesNotContain("\"word\":\"a\"", json);
        Assert.Contains("\"word\":\"b\"", json);
    }

    [Fact]
    public async Task ListSentences_ReturnsQuizSentences()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = "Idę do domu.",
            Translation = "I am going home.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { QuizId = quizId, UserId = "user-1" };

        var result = await tools.ExecuteAsync("list_sentences", "{}", context, CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("I am going home.", json);
        Assert.Contains("\"total_count\":1", json);
    }

    [Fact]
    public async Task DeleteSentence_QueuesPendingChangeWithSentenceText()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "Idę do domu.",
            Translation = "I am going home.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { QuizId = quizId, UserId = "user-1" };

        var result = await tools.ExecuteAsync(
            "delete_sentence",
            $$"""{"sentence_id":"{{sentenceId}}"}""",
            context,
            CancellationToken.None);

        var change = Assert.Single(context.PendingChanges);
        Assert.Equal(PendingChangeKinds.DeleteSentence, change.Kind);
        Assert.Equal(sentenceId, change.Payload.GetProperty("sentence_id").GetGuid());
        Assert.Equal("Idę do domu.", change.Payload.GetProperty("text").GetString());
        Assert.Contains("queued", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task DeleteSentence_UnknownIdReturnsError()
    {
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { QuizId = Guid.NewGuid(), UserId = "user-1" };

        var result = await tools.ExecuteAsync(
            "delete_sentence",
            $$"""{"sentence_id":"{{Guid.NewGuid()}}"}""",
            context,
            CancellationToken.None);

        Assert.Empty(context.PendingChanges);
        Assert.Contains("not found", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AddSentences_QueuesOnePendingChangePerSentence()
    {
        await using var db = CreateContext();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext
        {
            QuizId = Guid.NewGuid(),
            UserId = "user-1",
        };

        var result = await tools.ExecuteAsync(
            "add_sentences",
            """
            {
              "sentences": [
                { "text": "Idę do domu.", "translation": "I am going home." },
                { "text": "Ona czyta książkę.", "translation": "She is reading a book." }
              ]
            }
            """,
            context,
            CancellationToken.None);

        Assert.Equal(2, context.PendingChanges.Count);
        Assert.All(context.PendingChanges, change => Assert.Equal(PendingChangeKinds.AddSentence, change.Kind));
        Assert.Contains("\"count\":2", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task EditSentences_QueuesExistingSentencesAndReportsMissingOnes()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "Idę dom.",
            Translation = "I go home.",
        });
        await db.SaveChangesAsync();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { QuizId = quizId, UserId = "user-1" };
        var missingId = Guid.NewGuid();

        var result = await tools.ExecuteAsync(
            "edit_sentences",
            $$"""
            {
              "changes": [
                { "sentence_id": "{{sentenceId}}", "text": "Idę do domu." },
                { "sentence_id": "{{missingId}}", "translation": "Missing." }
              ]
            }
            """,
            context,
            CancellationToken.None);

        var change = Assert.Single(context.PendingChanges);
        Assert.Equal(PendingChangeKinds.EditSentence, change.Kind);
        Assert.Equal("Idę dom.", change.Payload.GetProperty("original_text").GetString());
        Assert.Equal("Idę do domu.", change.Payload.GetProperty("text").GetString());
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchWords_ReturnsOnlyMatchingWordsFromOwnedQuiz()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(new Quiz
        {
            Id = quizId,
            UserId = "user-1",
            Name = "German",
            SourceLanguage = "English",
            TargetLanguage = "German",
            Language = "German",
        });
        db.Words.AddRange(
            new Word { Id = "w1", QuizId = quizId, Lemma = "Haus", Translation = "house" },
            new Word { Id = "w2", QuizId = quizId, Lemma = "Baum", Translation = "tree" });
        await db.SaveChangesAsync();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { QuizId = quizId, UserId = "user-1" };

        var result = await tools.ExecuteAsync(
            "search_words",
            """{"query":"house"}""",
            context,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"word\":\"Haus\"", json);
        Assert.DoesNotContain("\"word\":\"Baum\"", json);
        Assert.Contains("\"total_count\":1", json);
    }

    [Fact]
    public async Task GetQuizSummary_ReturnsMetadataAndContentCounts()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        db.Collections.Add(new Collection
        {
            Id = collectionId,
            UserId = "user-1",
            Name = "Travel",
            Language = "Spanish",
        });
        db.Quizzes.Add(new Quiz
        {
            Id = quizId,
            UserId = "user-1",
            Name = "At the station",
            SourceLanguage = "English",
            TargetLanguage = "Spanish",
            Language = "Spanish",
            CollectionId = collectionId,
            IsPublic = true,
        });
        db.Words.Add(new Word { Id = "w1", QuizId = quizId, Lemma = "tren", Translation = "train" });
        db.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = "El tren llega pronto.",
            Translation = "The train arrives soon.",
        });
        await db.SaveChangesAsync();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { QuizId = quizId, UserId = "user-1" };

        var result = await tools.ExecuteAsync("get_quiz_summary", "{}", context, CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"name\":\"At the station\"", json);
        Assert.Contains("\"collection_name\":\"Travel\"", json);
        Assert.Contains("\"word_count\":1", json);
        Assert.Contains("\"sentence_count\":1", json);
        Assert.Contains("\"is_public\":true", json);
    }

    [Fact]
    public async Task LibraryOrganizationTools_QueueValidatedChanges()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        db.Collections.AddRange(
            new Collection
            {
                Id = sourceId,
                UserId = "user-1",
                Name = "Basics",
                Language = "French",
            },
            new Collection
            {
                Id = destinationId,
                UserId = "user-1",
                Name = "Course",
                Language = "French",
            });
        db.Quizzes.Add(new Quiz
        {
            Id = quizId,
            UserId = "user-1",
            Name = "Greetings",
            SourceLanguage = "English",
            TargetLanguage = "French",
            Language = "French",
            CollectionId = sourceId,
        });
        await db.SaveChangesAsync();
        var tools = new AssistantTools(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "French" };

        await tools.ExecuteAsync(
            "move_quiz",
            $$"""{"quiz_id":"{{quizId}}","collection_id":"{{destinationId}}"}""",
            context,
            CancellationToken.None);
        await tools.ExecuteAsync(
            "rename_collection",
            $$"""{"collection_id":"{{sourceId}}","name":"Foundations"}""",
            context,
            CancellationToken.None);
        await tools.ExecuteAsync(
            "move_collection",
            $$"""{"collection_id":"{{sourceId}}","parent_collection_id":"{{destinationId}}"}""",
            context,
            CancellationToken.None);

        Assert.Collection(
            context.PendingChanges,
            change => Assert.Equal(PendingChangeKinds.MoveQuiz, change.Kind),
            change => Assert.Equal(PendingChangeKinds.RenameCollection, change.Kind),
            change => Assert.Equal(PendingChangeKinds.MoveCollection, change.Kind));
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }
}
