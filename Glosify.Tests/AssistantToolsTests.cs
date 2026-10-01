using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantToolsTests
{
    private static readonly Guid QuizId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(AssistantMode.Language)]
    [InlineData(AssistantMode.Freestyle)]
    public void Every_schema_is_strict_and_the_list_is_stable(AssistantMode mode)
    {
        using var db = CreateContext();
        var toolbox = AssistantToolFactory.Create(db).Toolbox;

        var declarations = toolbox.Declarations(mode);

        Assert.Equal(declarations.Select(tool => tool.Name).Distinct(), declarations.Select(tool => tool.Name));
        Assert.Equal(JsonSerializer.Serialize(declarations), JsonSerializer.Serialize(toolbox.Declarations(mode)));
        Assert.All(declarations, tool =>
        {
            Assert.True(tool.Strict);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            var schema = JsonSerializer.SerializeToElement(tool.ParametersJsonSchema);
            Assert.DoesNotContain("$ref", schema.GetRawText());
            AssertStrict(schema, tool.Name);
        });
    }

    [Fact]
    public void Freestyle_mode_offers_items_and_no_language_material()
    {
        using var db = CreateContext();
        var toolbox = AssistantToolFactory.Create(db).Toolbox;

        var freestyle = toolbox.Declarations(AssistantMode.Freestyle);
        var add = JsonSerializer.SerializeToElement(freestyle.Single(tool => tool.Name == "add_items").ParametersJsonSchema);

        Assert.DoesNotContain(freestyle, tool => tool.Name is "list_saved_transcripts" or "get_saved_transcript");
        Assert.True(add.GetProperty("properties").TryGetProperty("items", out _));
        Assert.False(add.GetProperty("properties").TryGetProperty("sentences", out _));
    }

    [Fact]
    public async Task Add_items_cleans_the_batch_and_keeps_text_sent_as_both_a_sentence()
    {
        await using var db = await SeedAsync();
        var tools = AssistantToolFactory.Create(db);

        var result = await tools.RunAsync("add_items", Json(new
        {
            words = new[]
            {
                new { word = "dom", translation = "house" },
                new { word = "Dom ", translation = "home" },
                new { word = "To jest dom", translation = "This is a house" },
                new { word = " ", translation = "blank" },
            },
            sentences = new[] { new { text = "To jest dom.", translation = "This is a house." } },
        }), Context());

        Assert.False(result.IsError);
        Assert.Equal(QuizId, result.QuizId);
        Assert.Equal("Add 1 word and 1 sentence to “Polish basics”", result.Title);
        Assert.Equal([PendingChangeKinds.AddSentence, PendingChangeKinds.AddWord], result.Changes.Select(change => change.Kind));
        Assert.Equal(2, JsonSerializer.SerializeToElement(result.Output).GetProperty("skipped").GetArrayLength());
    }

    [Fact]
    public async Task Add_items_accepts_at_most_a_hundred_of_each_and_reports_the_rest()
    {
        await using var db = await SeedAsync();
        var tools = AssistantToolFactory.Create(db);

        var result = await tools.RunAsync("add_items", Json(new
        {
            words = Enumerable.Range(0, 105).Select(index => new { word = $"słowo{index}", translation = $"word {index}" }),
        }), Context());

        Assert.Equal(100, result.Changes.Count);
        Assert.Equal(5, JsonSerializer.SerializeToElement(result.Output).GetProperty("skipped").GetArrayLength());
    }

    [Theory]
    [InlineData(AssistantContentKind.Sentences, true)]
    [InlineData(AssistantContentKind.Words, false)]
    public async Task Add_items_refuses_the_content_type_the_user_did_not_ask_for(AssistantContentKind requested, bool sendWords)
    {
        await using var db = await SeedAsync();
        var tools = AssistantToolFactory.Create(db);

        var result = await tools.RunAsync("add_items", Json(sendWords
            ? new { words = new[] { new { word = "To jest dom.", translation = "This is a house." } } }
            : (object)new { sentences = new[] { new { text = "dom", translation = "house" } } }),
            Context() with { RequestedContentKind = requested });

        Assert.True(result.IsError);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task Content_tools_refuse_a_missing_or_foreign_quiz()
    {
        await using var db = await SeedAsync();
        var foreign = Guid.NewGuid();
        db.Quizzes.Add(new Quiz { Id = foreign, UserId = "other", Name = "Theirs", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" });
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var words = new[] { new { word = "dom", translation = "house" } };

        var none = await tools.RunAsync("add_items", Json(new { words }), Context() with { QuizId = null });
        var theirs = await tools.RunAsync("add_items", Json(new { quiz_id = foreign, words }), Context());

        Assert.Contains("No quiz is selected", Error(none));
        Assert.Contains("not found", Error(theirs));
    }

    [Fact]
    public async Task Edit_items_reads_originals_skips_unknown_ids_and_respects_the_focused_word()
    {
        await using var db = await SeedAsync(words: [("w1", "dom", "house"), ("w2", "kot", "cat")]);
        var tools = AssistantToolFactory.Create(db);

        var edit = await tools.RunAsync("edit_items", Json(new
        {
            words = new object[]
            {
                new { id = "w1", translation = "home" },
                new { id = "w2", word = "kot", translation = "cat" },
                new { id = "missing", translation = "x" },
            },
        }), Context());
        var focused = await tools.RunAsync("edit_items", Json(new { words = new[] { new { id = "w2", translation = "kitty" } } }),
            Context() with { FocusedWordId = "w1", FocusedWordLabel = "dom" });

        var change = Assert.Single(edit.Changes);
        Assert.Equal("house", change.Payload.GetProperty("original_translation").GetString());
        Assert.Equal("home", change.Payload.GetProperty("translation").GetString());
        Assert.Equal(2, JsonSerializer.SerializeToElement(edit.Output).GetProperty("skipped").GetArrayLength());
        Assert.True(focused.IsError);
        Assert.Contains("focused on dom", Error(focused));
    }

    [Fact]
    public async Task Delete_items_labels_what_it_removes_and_skips_unknown_ids()
    {
        await using var db = await SeedAsync(words: [("w1", "dom", "house")]);
        var tools = AssistantToolFactory.Create(db);

        var result = await tools.RunAsync("delete_items", Json(new { word_ids = new[] { "w1", "missing" } }), Context());

        var change = Assert.Single(result.Changes);
        Assert.Equal(PendingChangeKinds.DeleteWord, change.Kind);
        Assert.Equal("dom", change.Payload.GetProperty("word").GetString());
        Assert.Equal("Remove 1 word from “Polish basics”", result.Title);
    }

    [Fact]
    public async Task Create_quiz_fills_known_languages_and_validates_the_collection()
    {
        await using var db = await SeedAsync();
        var tools = AssistantToolFactory.Create(db);

        var created = await tools.RunAsync("create_quiz", Json(new { name = "Travel", words = new[] { new { word = "pociąg", translation = "train" } } }), Context());
        var badCollection = await tools.RunAsync("create_quiz", Json(new { name = "Travel", collection_id = Guid.NewGuid() }), Context());
        var noLanguage = await tools.RunAsync("create_quiz", Json(new { name = "Travel" }), Context() with { TargetLanguage = null });

        var payload = Assert.Single(created.Changes).Payload;
        Assert.Equal("Polish", payload.GetProperty("target_language").GetString());
        Assert.Equal("English", payload.GetProperty("source_language").GetString());
        Assert.Equal(1, payload.GetProperty("words").GetArrayLength());
        Assert.Contains("collection was not found", Error(badCollection));
        Assert.Contains("target_language is required", Error(noLanguage));
    }

    [Fact]
    public async Task Creation_keeps_the_practice_language_and_accepts_inferred_translations()
    {
        await using var db = await SeedAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = Context() with { SourceLanguage = "German" };
        var swedish = await tools.RunAsync("create_quiz", Json(new { name = "Resor", source_language = "Swedish",
            words = new[] { new { word = "pociąg", translation = "tåg" } } }), context);
        var fallback = await tools.RunAsync("create_quiz", Json(new { name = "Travel" }), context);
        var reversed = await tools.RunAsync("create_quiz", Json(new { name = "Travel", target_language = "English", source_language = "Polish" }), context);
        var missing = await tools.RunAsync("create_quiz", Json(new { name = "Travel", target_language = "Polish" }), context with { TargetLanguage = null });

        var payload = Assert.Single(swedish.Changes).Payload;
        Assert.Equal("Polish", payload.GetProperty("target_language").GetString());
        Assert.Equal("Swedish", payload.GetProperty("source_language").GetString());
        Assert.Equal("pociąg", payload.GetProperty("words")[0].GetProperty("word").GetString());
        Assert.Equal("tåg", payload.GetProperty("words")[0].GetProperty("translation").GetString());
        Assert.Equal("English", Assert.Single(fallback.Changes).Payload.GetProperty("source_language").GetString());
        Assert.True(reversed.IsError);
        Assert.Empty(reversed.Changes);
        Assert.True(missing.IsError);
        Assert.Empty(missing.Changes);
    }

    [Fact]
    public async Task Freestyle_quizzes_take_items_and_ignore_languages()
    {
        await using var db = await SeedAsync();
        var tools = AssistantToolFactory.Create(db);

        var result = await tools.RunAsync("create_quiz", Json(new { name = "Biology", items = new[] { new { prompt = "Cell unit?", answer = "The cell" } } }),
            Context() with { Mode = AssistantMode.Freestyle, TargetLanguage = "Freestyle" });

        var payload = Assert.Single(result.Changes).Payload;
        Assert.Equal("Freestyle", payload.GetProperty("target_language").GetString());
        Assert.Equal("Cell unit?", payload.GetProperty("words")[0].GetProperty("word").GetString());
    }

    [Fact]
    public async Task Collections_cannot_move_into_their_own_descendants_or_clash_on_names()
    {
        await using var db = await SeedAsync();
        var parent = new Collection { Id = Guid.NewGuid(), UserId = "user", Name = "Parent", Language = "Polish" };
        var child = new Collection { Id = Guid.NewGuid(), UserId = "user", Name = "Child", Language = "Polish", ParentCollectionId = parent.Id };
        var sibling = new Collection { Id = Guid.NewGuid(), UserId = "user", Name = "Sibling", Language = "Polish" };
        db.Collections.AddRange(parent, child, sibling);
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);

        var cycle = await tools.RunAsync("move_collection", Json(new { collection_id = parent.Id, parent_collection_id = child.Id }), Context());
        var clash = await tools.RunAsync("rename_collection", Json(new { collection_id = sibling.Id, name = "Parent" }), Context());
        var move = await tools.RunAsync("move_collection", Json(new { collection_id = sibling.Id, parent_collection_id = parent.Id }), Context());

        Assert.Contains("inside itself", Error(cycle));
        Assert.Contains("already exists", Error(clash));
        Assert.Equal(PendingChangeKinds.MoveCollection, Assert.Single(move.Changes).Kind);
    }

    [Fact]
    public async Task Search_items_finds_words_and_sentences_regardless_of_case()
    {
        await using var db = await SeedAsync(words: [("w1", "Dom", "house"), ("w2", "kot", "cat")]);
        db.QuizSentences.Add(new QuizSentence { Id = Guid.NewGuid(), QuizId = QuizId, Text = "Mój dom jest duży.", Translation = "My house is big." });
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);

        var output = JsonSerializer.SerializeToElement(await tools.ExecuteAsync("search_items", Json(new { query = "HOUSE" }), Context()));

        Assert.Equal("Dom", output.GetProperty("words")[0].GetProperty("word").GetString());
        Assert.Equal("Mój dom jest duży.", output.GetProperty("sentences")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_book_search_miss_says_which_term_is_absent()
    {
        await using var db = await SeedAsync();
        var bookId = Guid.NewGuid();
        db.BookDocuments.Add(new BookDocument { Id = bookId, UserId = "user", Title = "Course book", PageCount = 1, BlobName = "b", OriginalFileName = "b.pdf" });
        db.BookPages.Add(new BookPage { Id = Guid.NewGuid(), BookDocumentId = bookId, PageNumber = 1, Text = "Rozdział pierwszy: dom i rodzina." });
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);

        var output = JsonSerializer.SerializeToElement(await tools.ExecuteAsync("search_book_pages", Json(new { query = "dom samochód", book_id = bookId }), Context()));

        Assert.Equal(0, output.GetProperty("match_count").GetInt32());
        Assert.Contains("samochód appear nowhere", output.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task Read_source_returns_numbered_lines_within_bounds()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = Context() with { Source = new SourceText(string.Join("\n", Enumerable.Range(1, 10).Select(line => $"line {line}"))) };

        var read = await tools.RunAsync("read_source", Json(new { from_line = 3, to_line = 4 }), context);
        var beyond = await tools.RunAsync("read_source", Json(new { from_line = 11 }), context);
        var none = await tools.RunAsync("read_source", Json(new { from_line = 1 }), Context());

        Assert.Equal("3: line 3\n4: line 4\n", JsonSerializer.SerializeToElement(read.Output).GetProperty("lines").GetString()!.ReplaceLineEndings("\n"));
        Assert.Equal(new ReadSourceEffect(3, 4), read.Effect);
        Assert.Contains("between 1 and 10", Error(beyond));
        Assert.Contains("no stored source text", Error(none));
    }

    [Fact]
    public async Task Plan_and_question_tools_describe_their_effect()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);

        var plan = await tools.RunAsync("update_plan", Json(new { items = new object[] { new { text = "Read", status = "completed" }, new { text = "Add", status = "in_progress" } } }), Context());
        var ask = await tools.RunAsync("ask_user", Json(new { question = "Which topic?", options = new[] { "Food", "food", "Travel" }, multiple = true }), Context());
        var single = await tools.RunAsync("ask_user", Json(new { question = "Which topic?", options = new[] { "Food" }, multiple = true }), Context());

        Assert.Equal("Updated the plan (1/2 done)", plan.Title);
        Assert.Equal("in_progress", Assert.IsType<UpdatePlanEffect>(plan.Effect).Items[1].Status);
        var question = Assert.IsType<AskUserEffect>(ask.Effect);
        Assert.Equal(["Food", "Travel"], question.Options);
        Assert.True(question.Multiple);
        Assert.False(Assert.IsType<AskUserEffect>(single.Effect).Multiple);
    }

    [Fact]
    public async Task Invalid_arguments_come_back_as_a_correctable_error()
    {
        await using var db = await SeedAsync();
        var tools = AssistantToolFactory.Create(db);

        var missing = await tools.RunAsync("search_items", "{}", Context());
        var malformed = await tools.RunAsync("search_items", "{\"query\":", Context());
        var unknown = await tools.RunAsync("search_items", Json(new { query = "dom", extra = 1 }), Context());

        Assert.All([missing, malformed, unknown], result =>
        {
            Assert.True(result.IsError);
            Assert.Contains("Rewrite the call", Error(result));
        });
    }

    private static void AssertStrict(JsonElement schema, string tool)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("enum", out _))
        {
            Assert.True(schema.TryGetProperty("type", out _), $"{tool}: an enum without a type");
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            var names = properties.EnumerateObject().Select(property => property.Name).Order().ToArray();
            var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!).Order().ToArray();
            Assert.Equal(names, required);
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean(), $"{tool}: additional properties allowed");
            foreach (var property in properties.EnumerateObject())
            {
                AssertStrict(property.Value, tool);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            AssertStrict(items, tool);
        }
    }

    private static ToolContext Context() => new()
    {
        UserId = "user",
        Mode = AssistantMode.Language,
        QuizId = QuizId,
        TargetLanguage = "Polish",
        TargetLanguageCode = "pl",
        SourceLanguage = "English",
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Error(ToolResult result) =>
        JsonSerializer.SerializeToElement(result.Output).GetProperty("error").GetString()!;

    private static GlosifyContext CreateContext() =>
        new(new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<GlosifyContext> SeedAsync(params (string Id, string Word, string Translation)[] words)
    {
        var db = CreateContext();
        db.Quizzes.Add(new Quiz { Id = QuizId, UserId = "user", Name = "Polish basics", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" });
        foreach (var (id, word, translation) in words)
        {
            db.Words.Add(new Word { Id = id, QuizId = QuizId, Lemma = word, Translation = translation });
        }

        await db.SaveChangesAsync();
        return db;
    }
}
