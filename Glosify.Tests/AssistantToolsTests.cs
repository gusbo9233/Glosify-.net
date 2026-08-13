using System.Diagnostics;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class AssistantToolsTests
{
    [Fact]
    public void Declarations_SeparateVocabularyAndCustomQuizCreation()
    {
        using var db = CreateContext();
        var names = AssistantToolFactory.Create(db).GlobalDeclarations.Select(tool => tool.Name).ToList();

        Assert.Contains("create_vocabulary_quiz", names);
        Assert.Contains("create_custom_quiz", names);
        Assert.Contains("create_custom_quiz_from_content", names);
        Assert.Contains("list_custom_quiz_templates", names);
        Assert.Contains("add_label", names);
        Assert.Contains("add_text_input", names);
        Assert.Contains("add_submit_button", names);
        Assert.Contains("add_feedback_message", names);
        Assert.DoesNotContain("add_custom_quiz_elements", names);
        Assert.DoesNotContain("create_quiz", names);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{not-json")]
    public async Task CreateVocabularyQuiz_TreatsNonObjectArgumentsAsMissingFields(string argsJson)
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            argsJson,
            context,
            CancellationToken.None));

        Assert.Equal("name and source_language are required.", result.GetProperty("error").GetString());
        Assert.Empty(context.PendingChanges);
    }

    // Scoping the creator's tools is what makes "generate exercises" unable to fork a new
    // custom quiz: the tool that would do it is not on the agent at all.
    [Fact]
    public void CustomQuizBuilderDeclarations_ExcludeEverythingThatCouldLeaveTheOpenQuiz()
    {
        using var db = CreateContext();
        var names = AssistantToolFactory.Create(db).CustomQuizBuilderDeclarations.Select(tool => tool.Name).ToList();

        Assert.DoesNotContain("create_custom_quiz", names);
        Assert.DoesNotContain("create_custom_quiz_from_content", names);
        Assert.DoesNotContain("create_vocabulary_quiz", names);
        Assert.DoesNotContain("create_quiz", names);
        Assert.DoesNotContain("create_collection", names);
        Assert.DoesNotContain("move_quiz", names);

        Assert.Contains("get_custom_quiz", names);
        Assert.Contains("add_text_input", names);
        Assert.Contains("add_submit_button", names);
        Assert.Contains("add_feedback_message", names);
        Assert.Contains("configure_custom_quiz_element", names);
        Assert.Contains("remove_custom_quiz_element", names);
        // Word bindings can only reference words already in the backing quiz, so the
        // builder still needs to look them up.
        Assert.Contains("list_words", names);
        Assert.Contains("search_words", names);
    }

    [Fact]
    public void CustomQuizBuilderDeclarations_StayWellUnderTheGeneralToolSurface()
    {
        using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);

        var builder = tools.CustomQuizBuilderDeclarations.Count;
        var general = tools.GlobalDeclarations.Count + tools.Declarations.Count;

        Assert.True(builder < general / 2, $"Builder surface {builder} should be far smaller than {general}.");
    }

    [Fact]
    public async Task CustomQuizTemplates_AreDiscoverableAndSetCreationStyle()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId, CurrentLanguage = "Polish" };

        var listed = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "list_custom_quiz_templates", "{}", context, CancellationToken.None));
        await tools.ExecuteAsync("create_custom_quiz", """{"name":"Styled","template_id":"aurora_cards"}""", context, CancellationToken.None);

        Assert.Equal(4, listed.GetProperty("count").GetInt32());
        Assert.Contains(listed.GetProperty("templates").EnumerateArray(), template =>
            template.GetProperty("id").GetString() == "aurora_cards");
        Assert.Equal("aurora", Assert.Single(context.PendingChanges).Payload.GetProperty("style_preset").GetString());
    }

    // Creating a second custom quiz while one is open in the creator stranded the open
    // editor: PendingCustomQuizRef wins over the open quiz, so every later element call
    // in the turn went to the new draft instead.
    [Fact]
    public async Task CreateCustomQuiz_RefusesWhileACustomQuizIsOpenInTheCreator()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var openCustomQuizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId, CustomQuizId = openCustomQuizId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "create_custom_quiz", """{"name":"Second quiz"}""", context, CancellationToken.None));

        Assert.Equal(openCustomQuizId, result.GetProperty("open_custom_quiz_id").GetGuid());
        Assert.Empty(context.PendingChanges);
        Assert.Null(context.PendingCustomQuizRef);
    }

    [Fact]
    public async Task CreateCustomQuiz_AllowsASecondQuizWhenExplicitlyRequested()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId, CustomQuizId = Guid.NewGuid() };

        await tools.ExecuteAsync(
            "create_custom_quiz",
            """{"name":"Second quiz","create_additional_quiz":true}""",
            context,
            CancellationToken.None);

        Assert.Equal("Second quiz", Assert.Single(context.PendingChanges).Payload.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateCustomQuiz_QueuesNormallyWhenNoCustomQuizIsOpen()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId };

        await tools.ExecuteAsync("create_custom_quiz", """{"name":"Drills"}""", context, CancellationToken.None);

        Assert.Equal("Drills", Assert.Single(context.PendingChanges).Payload.GetProperty("name").GetString());
        Assert.NotNull(context.PendingCustomQuizRef);
    }

    // Inspecting a quiz queued earlier in the turn used to return the open quiz or an
    // error, which read as "the new quiz isn't ready" and stopped the model mid-build.
    [Fact]
    public async Task GetCustomQuiz_DescribesAQuizStillQueuedInThisTurn()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId };

        await tools.ExecuteAsync("create_custom_quiz", """{"name":"Verb drills"}""", context, CancellationToken.None);
        await tools.ExecuteAsync(
            "add_text_input",
            """{"id":"row1","label":"1. ja bed{{blank}}","expected_text":"e"}""",
            context,
            CancellationToken.None);

        var inspected = JsonSerializer.SerializeToElement(
            await tools.ExecuteAsync("get_custom_quiz", "{}", context, CancellationToken.None));

        Assert.True(inspected.GetProperty("queued").GetBoolean());
        Assert.Equal("Verb drills", inspected.GetProperty("name").GetString());
        Assert.Equal(1, inspected.GetProperty("element_count").GetInt32());
        Assert.Equal("row1", inspected.GetProperty("elements")[0].GetProperty("id").GetString());
        // It must still report what the document is missing to be playable.
        var errors = inspected.GetProperty("validation_errors").EnumerateArray()
            .Select(error => error.GetString()).ToList();
        Assert.Contains("Add exactly one submit button.", errors);
        Assert.Contains("Add exactly one feedback message.", errors);
    }

    [Fact]
    public async Task GetCustomQuiz_StillReadsAStoredQuizWhenNoDraftIsQueued()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var customQuizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.CustomQuizzes.Add(new Glosify.Models.Entities.CustomQuiz
        {
            Id = customQuizId,
            QuizId = quizId,
            Name = "Stored quiz",
            DefinitionJson = """{"schemaVersion":1,"stylePreset":"editorial","blocks":[]}""",
            SchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId, CustomQuizId = customQuizId };

        var inspected = JsonSerializer.SerializeToElement(await AssistantToolFactory.Create(db)
            .ExecuteAsync("get_custom_quiz", "{}", context, CancellationToken.None));

        Assert.False(inspected.TryGetProperty("queued", out _));
        Assert.Equal("Stored quiz", inspected.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateQuiz_QueuesBundledCustomQuizFromStarterWords()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        await tools.ExecuteAsync(
            "create_quiz",
            """
            {
              "name": "Page vocabulary",
              "source_language": "English",
              "words": [{ "word": "dom", "translation": "house" }],
              "custom_quiz": {
                "name": "Page practice",
                "blocks": [
                  { "id": "answer", "type": "text_input", "label": "Translate house", "expected_binding": { "word": "dom", "field": "lemma" } },
                  { "id": "submit", "type": "submit_button", "text": "Check" },
                  { "id": "feedback", "type": "feedback_message" }
                ]
              }
            }
            """,
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal("Page practice", payload.GetProperty("custom_quiz").GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateCustomQuizFromContent_QueuesShellThenIndividualElements()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        await tools.ExecuteAsync(
            "create_custom_quiz_from_content",
            """
            {
              "quiz_name": "Verb endings source",
              "custom_quiz_name": "Verb endings",
              "source_language": "English",
              "words": [{ "word": "być", "translation": "to be" }]
            }
            """,
            context,
            CancellationToken.None);
        await tools.ExecuteAsync("add_label", """{"id":"instructions","text":"Write the ending"}""", context, CancellationToken.None);
        await tools.ExecuteAsync("add_text_input", """{"id":"answer","label":"1. ja będ{{blank}} jutro w domu.","expected_text":"ę"}""", context, CancellationToken.None);
        await tools.ExecuteAsync("add_submit_button", """{"id":"submit","text":"Check"}""", context, CancellationToken.None);
        await tools.ExecuteAsync("add_feedback_message", """{"id":"feedback"}""", context, CancellationToken.None);

        Assert.Equal(5, context.PendingChanges.Count);
        Assert.Equal(PendingChangeKinds.CreateQuiz, context.PendingChanges[0].Kind);
        Assert.All(context.PendingChanges.Skip(1), change => Assert.Equal(PendingChangeKinds.AddCustomQuizElement, change.Kind));
        var answer = context.PendingChanges[2].Payload.GetProperty("block");
        Assert.Equal("ę", answer.GetProperty("expected_text").GetString());
        Assert.Equal(context.PendingChanges[0].Payload.GetProperty("custom_quiz").GetProperty("draft_ref").GetString(),
            context.PendingChanges[2].Payload.GetProperty("custom_quiz_ref").GetString());
    }

    [Fact]
    public async Task AtomicTextInputs_RejectMissingDuplicateAndDrawnBlankLabels()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };
        await tools.ExecuteAsync("create_custom_quiz_from_content", """
            {"quiz_name":"Verb source","custom_quiz_name":"Verb questions","source_language":"English","words":[{"word":"być","translation":"to be"}]}
            """, context, CancellationToken.None);
        var missing = await tools.ExecuteAsync("add_text_input", """{"id":"q1","expected_text":"ę"}""", context, CancellationToken.None);
        var drawnBlank = await tools.ExecuteAsync("add_text_input", """{"id":"q0","label":"ja będ___","expected_text":"ę"}""", context, CancellationToken.None);
        await tools.ExecuteAsync("add_text_input", """{"id":"q1","label":"ja będ{{blank}}","expected_text":"ę"}""", context, CancellationToken.None);
        var duplicate = await tools.ExecuteAsync("add_text_input", """{"id":"q2","label":"ja będ{{blank}}","expected_text":"esz"}""", context, CancellationToken.None);

        Assert.Equal(2, context.PendingChanges.Count);
        Assert.Contains("invalid_custom_quiz_questions", JsonSerializer.Serialize(missing));
        Assert.Contains("{{blank}}", JsonSerializer.Serialize(drawnBlank));
        Assert.Contains("invalid_custom_quiz_questions", JsonSerializer.Serialize(duplicate));
    }

    [Fact]
    public async Task AddCustomQuizElements_UsesOpenCustomQuizContext()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var customQuizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.CustomQuizzes.Add(new CustomQuiz
        {
            Id = customQuizId,
            QuizId = quizId,
            Name = "Builder",
            DefinitionJson = "{\"schemaVersion\":1,\"blocks\":[]}",
            SchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            QuizId = quizId,
            CustomQuizId = customQuizId,
            UserId = "user-1",
        };

        await tools.ExecuteAsync(
            "add_custom_quiz_elements",
            """{"blocks":[{"id":"heading","type":"quiz_heading","text":"Practice"}]}""",
            context,
            CancellationToken.None);

        var change = Assert.Single(context.PendingChanges);
        Assert.Equal(PendingChangeKinds.AddCustomQuizElements, change.Kind);
        Assert.Equal(customQuizId, change.Payload.GetProperty("custom_quiz_id").GetGuid());
    }

    [Fact]
    public async Task CreateQuiz_QueuesPendingChangeWithCurrentLanguageDefault()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
    public async Task CreateVocabularyQuiz_QueuesStarterWordsAndSentencesSeparately()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """
            {"name":"Travel Polish","source_language":"English",
             "words":[{"word":"pociag","translation":"train"}],
             "sentences":[{"text":"Pociag odjezdza o osmej.","translation":"The train leaves at eight."}]}
            """,
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        var word = Assert.Single(payload.GetProperty("words").EnumerateArray().ToArray());
        Assert.Equal("pociag", word.GetProperty("word").GetString());
        var sentence = Assert.Single(payload.GetProperty("sentences").EnumerateArray().ToArray());
        Assert.Equal("Pociag odjezdza o osmej.", sentence.GetProperty("text").GetString());
        Assert.Equal("The train leaves at eight.", sentence.GetProperty("translation").GetString());
    }

    [Fact]
    public async Task CreateVocabularyQuiz_AllowsWordOnlyPayload()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """{"name":"Travel Polish","source_language":"English","words":[{"word":"dom","translation":"house"}]}""",
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Single(payload.GetProperty("words").EnumerateArray().ToArray());
        Assert.Empty(payload.GetProperty("sentences").EnumerateArray().ToArray());
    }

    // "a quiz with words and example sentences" resolves to Both, which legitimately permits
    // either kind, so the content guard cannot catch a sentence sent in both arrays. Without
    // the cross-check it was stored twice: once as vocabulary, once as a sentence.
    [Fact]
    public async Task CreateVocabularyQuiz_DoesNotStoreASentenceAsVocabularyToo()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Polish",
            RequestedContentKind = AssistantContentKind.Both,
        };

        var result = await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """
            {"name":"Travel Polish","source_language":"English",
             "words":[{"word":"dom","translation":"house"},
                      {"word":"To jest  moj dom","translation":"This is my house."}],
             "sentences":[{"text":"To jest moj dom.","translation":"This is my house."}]}
            """,
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        var word = Assert.Single(payload.GetProperty("words").EnumerateArray().ToArray());
        Assert.Equal("dom", word.GetProperty("word").GetString());
        Assert.Single(payload.GetProperty("sentences").EnumerateArray().ToArray());
        Assert.Contains("skipped_words", JsonSerializer.Serialize(result));
    }

    // The cross-check matches text exactly. It must not start removing multiword vocabulary
    // that merely resembles the sentences alongside it.
    [Fact]
    public async Task CreateVocabularyQuiz_KeepsPhrasesThatAreNotAlsoSentences()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """
            {"name":"Travel Polish","source_language":"English",
             "words":[{"word":"by the way","translation":"nawiasem mowiac"}],
             "sentences":[{"text":"By the way, I am late.","translation":"Nawiasem mowiac, jestem spozniony."}]}
            """,
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        var word = Assert.Single(payload.GetProperty("words").EnumerateArray().ToArray());
        Assert.Equal("by the way", word.GetProperty("word").GetString());
    }

    // Parse failures and cap overflow both report positions in the request array. Building the
    // source map from a list that the cap had already appended to mixed two coordinate systems
    // and named an unrelated word.
    [Fact]
    public async Task CreateVocabularyQuiz_ReportsEverySkipAgainstTheRequestIndex()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };
        // One invalid entry, then 101 valid words: request indexes 1..101. The duplicate is
        // request index 50, and the 101st valid word overflows the 100-item cap.
        var words = new List<string> { """{"word":"","translation":"invalid"}""" };
        for (var i = 1; i <= 101; i++)
        {
            words.Add($$"""{"word":"w{{i}}","translation":"t{{i}}"}""");
        }

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            $$"""
            {"name":"Big","source_language":"English",
             "words":[{{string.Join(",", words)}}],
             "sentences":[{"text":"w50","translation":"fiftieth"}]}
            """,
            context,
            CancellationToken.None));

        var skipped = result.GetProperty("skipped_words").EnumerateArray()
            .Select(item => item.GetProperty("Index").GetInt32())
            .ToArray();
        // 0 = the invalid entry, 101 = the capped overflow word, 50 = the duplicate.
        Assert.Equal([0, 101, 50], skipped);
    }

    [Fact]
    public async Task CreateVocabularyQuiz_SkipsInvalidStarterSentences()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        var result = await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """
            {"name":"Travel Polish","source_language":"English",
             "sentences":[{"text":"To jest dom.","translation":"This is a house."},
                          {"text":"Brakuje tlumaczenia."}]}
            """,
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Single(payload.GetProperty("sentences").EnumerateArray().ToArray());
        Assert.Contains("skipped_sentences", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task CreateVocabularyQuiz_DefaultsSourceLanguageFromContext()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Polish",
            SourceLanguage = "English",
        };

        await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """{"name":"Travel Polish"}""",
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal("English", payload.GetProperty("source_language").GetString());
    }

    [Fact]
    public async Task CreateVocabularyQuiz_StillRequiresASourceLanguageFromSomewhere()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        var result = await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """{"name":"Travel Polish"}""",
            context,
            CancellationToken.None);

        Assert.Empty(context.PendingChanges);
        Assert.Contains("source_language", JsonSerializer.Serialize(result));
    }

    // Creation carries both content types at once, so it needs the guard the add tools have.
    [Fact]
    public async Task WordIntent_RejectsStarterSentencesOnCreation()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Polish",
            RequestedContentKind = AssistantContentKind.Words,
        };

        var result = await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """
            {"name":"Travel Polish","source_language":"English",
             "words":[{"word":"dom","translation":"house"}],
             "sentences":[{"text":"To jest dom.","translation":"This is a house."}]}
            """,
            context,
            CancellationToken.None);

        Assert.Empty(context.PendingChanges);
        Assert.Contains("word", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BothIntent_AllowsStarterWordsAndSentencesOnCreation()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Polish",
            RequestedContentKind = AssistantContentKind.Both,
        };

        await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """
            {"name":"Travel Polish","source_language":"English",
             "words":[{"word":"dom","translation":"house"}],
             "sentences":[{"text":"To jest dom.","translation":"This is a house."}]}
            """,
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Single(payload.GetProperty("words").EnumerateArray().ToArray());
        Assert.Single(payload.GetProperty("sentences").EnumerateArray().ToArray());
    }

    // A custom quiz binds its elements to starter words, so those words are structure rather
    // than requested vocabulary. Blocking them on a sentence request would make "an interactive
    // quiz with sentences from this page" impossible to build.
    [Fact]
    public async Task SentenceIntent_StillAllowsStructuralWordsOnACustomQuizCreation()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Polish",
            RequestedContentKind = AssistantContentKind.Sentences,
        };

        await tools.ExecuteAsync(
            "create_vocabulary_quiz",
            """
            {"name":"Chapter 3","source_language":"English",
             "words":[{"word":"dom","translation":"house"}],
             "custom_quiz":{"name":"Chapter 3 drill","blocks":[
                {"type":"text_input","id":"q1","label":"1. To jest moj {{blank}}","expected_text":"dom"}]}}
            """,
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Single(payload.GetProperty("words").EnumerateArray().ToArray());
    }

    // The prompt tells the model it may omit a language the conversation established. That has
    // to hold for the custom-quiz creation path too, or the turn dies on a required field.
    [Fact]
    public async Task CreateCustomQuizFromContent_DefaultsSourceLanguageFromContext()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            CurrentLanguage = "Polish",
            SourceLanguage = "English",
        };

        await tools.ExecuteAsync(
            "create_custom_quiz_from_content",
            """
            {"quiz_name":"Chapter 3","custom_quiz_name":"Chapter 3 drill",
             "words":[{"word":"dom","translation":"house"}]}
            """,
            context,
            CancellationToken.None);

        var payload = context.PendingChanges[0].Payload;
        Assert.Equal("English", payload.GetProperty("source_language").GetString());
    }

    // Adding to an existing quiz spreads the two content types across separate calls, so the
    // same text proposed as both must not be queued twice within one turn.
    [Fact]
    public async Task AddWords_DoesNotQueueAWordAlreadyProposedAsASentence()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId };

        await tools.ExecuteAsync(
            "add_sentences",
            """{"sentences":[{"text":"To jest moj dom.","translation":"This is my house."}]}""",
            context,
            CancellationToken.None);
        var result = await tools.ExecuteAsync(
            "add_words",
            """
            {"words":[{"word":"dom","translation":"house"},
                      {"word":"To jest  moj dom","translation":"This is my house."}]}
            """,
            context,
            CancellationToken.None);

        var kinds = context.PendingChanges.Select(change => change.Kind).ToArray();
        Assert.Equal([PendingChangeKinds.AddSentence, PendingChangeKinds.AddWord], kinds);
        Assert.Equal(
            "dom",
            context.PendingChanges[1].Payload.GetProperty("word").GetString());
        Assert.Contains("already proposed as a sentence", JsonSerializer.Serialize(result));
    }

    // The skipped list mixes parse failures and duplicate drops, so both must index the
    // request the model sent rather than the compacted list of valid drafts.
    [Fact]
    public async Task AddWords_ReportsSkippedDuplicatesAgainstTheRequestIndex()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId };

        await tools.ExecuteAsync(
            "add_sentences",
            """{"sentences":[{"text":"To jest moj dom.","translation":"This is my house."}]}""",
            context,
            CancellationToken.None);
        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "add_words",
            """
            {"words":[{"word":"","translation":"invalid, dropped by parsing"},
                      {"word":"To jest moj dom.","translation":"This is my house."},
                      {"word":"dom","translation":"house"}]}
            """,
            context,
            CancellationToken.None));

        var skipped = result.GetProperty("skipped").EnumerateArray().ToArray();
        Assert.Equal(2, skipped.Length);
        Assert.Equal(0, skipped[0].GetProperty("Index").GetInt32());
        // The duplicate is item 1 of the request, not item 0 of the compacted valid list.
        Assert.Equal(1, skipped[1].GetProperty("Index").GetInt32());
    }

    [Fact]
    public async Task AddWord_RefusesASingleWordAlreadyProposedAsASentence()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", QuizId = quizId };

        await tools.ExecuteAsync(
            "add_sentence",
            """{"text":"To jest moj dom.","translation":"This is my house."}""",
            context,
            CancellationToken.None);
        var result = await tools.ExecuteAsync(
            "add_word",
            """{"word":"To jest moj dom.","translation":"This is my house."}""",
            context,
            CancellationToken.None);

        Assert.Single(context.PendingChanges);
        Assert.Contains("already proposed as a sentence", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task SentenceIntent_RejectsWordStorageAtTheExecutionBoundary()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            QuizId = quizId,
            RequestedContentKind = AssistantContentKind.Sentences,
        };

        var single = await tools.ExecuteAsync(
            "add_word",
            """{"word":"To jest moj dom.","translation":"This is my house."}""",
            context,
            CancellationToken.None);
        var batch = await tools.ExecuteAsync(
            "add_words",
            """{"words":[{"word":"To jest moj dom.","translation":"This is my house."}]}""",
            context,
            CancellationToken.None);

        Assert.Empty(context.PendingChanges);
        Assert.Contains("sentence", JsonSerializer.Serialize(single), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sentence", JsonSerializer.Serialize(batch), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WordIntent_RejectsSentenceStorageAtTheExecutionBoundary()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            QuizId = quizId,
            RequestedContentKind = AssistantContentKind.Words,
        };

        var result = await tools.ExecuteAsync(
            "add_sentences",
            """{"sentences":[{"text":"To jest dom.","translation":"This is a house."}]}""",
            context,
            CancellationToken.None);

        Assert.Empty(context.PendingChanges);
        Assert.Contains("word", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BothIntent_AllowsWordsAndSentencesTogether()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            QuizId = quizId,
            RequestedContentKind = AssistantContentKind.Both,
        };

        await tools.ExecuteAsync(
            "add_word",
            """{"word":"dom","translation":"house"}""",
            context,
            CancellationToken.None);
        await tools.ExecuteAsync(
            "add_sentence",
            """{"text":"To jest dom.","translation":"This is a house."}""",
            context,
            CancellationToken.None);

        Assert.Equal(
            [PendingChangeKinds.AddWord, PendingChangeKinds.AddSentence],
            context.PendingChanges.Select(change => change.Kind));
    }

    [Fact]
    public async Task MultiwordPhrase_IsStillStoredAsAWord()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext
        {
            UserId = "user-1",
            QuizId = quizId,
            RequestedContentKind = AssistantContentKind.Words,
        };

        await tools.ExecuteAsync(
            "add_word",
            """{"word":"by the way","translation":"nawiasem mowiac"}""",
            context,
            CancellationToken.None);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal("by the way", payload.GetProperty("word").GetString());
    }

    [Fact]
    public async Task CreateCollection_QueuesPendingChangeWithCurrentLanguageDefault()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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

        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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
        var tools = AssistantToolFactory.Create(db);
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

    [Fact]
    public async Task GetBookPages_ReadsARunOfPagesInOrderFromTheSelectedBook()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "user-1", pageCount: 6);
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_book_pages", """{"from_page":2,"limit":3}""", context, CancellationToken.None));

        var pages = result.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal([2, 3, 4], pages.Select(page => page.GetProperty("page_number").GetInt32()));
        Assert.Equal("Page 2 text.", pages[0].GetProperty("text").GetString());
        Assert.True(result.GetProperty("has_more").GetBoolean());
        Assert.Equal(5, result.GetProperty("next_page").GetInt32());
    }

    [Fact]
    public async Task GetBookPages_ReportsNoMoreOnTheLastPage()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "user-1", pageCount: 3);
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_book_pages", """{"from_page":3}""", context, CancellationToken.None));

        Assert.Single(result.GetProperty("pages").EnumerateArray());
        Assert.False(result.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task GetBookPages_RejectsAnotherUsersBook()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "owner", pageCount: 2);
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "intruder", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_book_pages", "{}", context, CancellationToken.None));

        Assert.Equal("Book not found.", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetBookPages_NeedsABookIdWhenNoneIsSelected()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1" };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_book_pages", "{}", context, CancellationToken.None));

        Assert.Equal("Choose a book first or provide a valid book_id.", result.GetProperty("error").GetString());
    }

    // A single call must not be able to fill the context window, however long the pages
    // are. The first page always comes back so the model never gets an empty answer.
    [Fact]
    public async Task GetBookPages_StopsAtTheCharacterBudget()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "user-1", pageCount: 4, pageText: new string('a', 7_000));
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_book_pages", """{"from_page":1,"limit":4}""", context, CancellationToken.None));

        Assert.Single(result.GetProperty("pages").EnumerateArray());
        Assert.Equal(2, result.GetProperty("next_page").GetInt32());
        Assert.True(result.GetProperty("has_more").GetBoolean());
    }

    // The reason this tool exists: a book runs to hundreds of pages, so the model has to be
    // able to find page 140 without paging there three pages at a time from page 1.
    [Fact]
    public async Task SearchBookPages_FindsMatchesDeepInTheBook()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "user-1", pageCount: 200);
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"Page 140"}""", context, CancellationToken.None));

        var match = Assert.Single(result.GetProperty("matches").EnumerateArray());
        Assert.Equal(140, match.GetProperty("page_number").GetInt32());
        Assert.Contains("Page 140 text.", match.GetProperty("snippet").GetString());
        Assert.Equal(1, result.GetProperty("match_count").GetInt32());
        Assert.False(result.GetProperty("has_more").GetBoolean());
    }

    // Several words are an AND, so a page holding only one of them is not a match.
    [Fact]
    public async Task SearchBookPages_RequiresEveryTermOnTheSamePage()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1",
            "Odmiana czasownika w czasie przyszłym.",
            "Odmiana rzeczownika.",
            "Czasownik nieregularny.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"odmiana czasownika"}""", context, CancellationToken.None));

        var match = Assert.Single(result.GetProperty("matches").EnumerateArray());
        Assert.Equal(1, match.GetProperty("page_number").GetInt32());
    }

    [Fact]
    public async Task SearchBookPages_CapsMatchesAndReportsMore()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "user-1", pageCount: 30, pageText: "shared text");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"shared","limit":5}""", context, CancellationToken.None));

        Assert.Equal(5, result.GetProperty("matches").GetArrayLength());
        Assert.Equal(30, result.GetProperty("match_count").GetInt32());
        Assert.True(result.GetProperty("has_more").GetBoolean());
    }

    // The whole point of ranking: a page late in the book that is really about the term
    // must outrank earlier pages that mention it once, or the tool reintroduces the
    // "assistant only sees the beginning" bug one layer up.
    [Fact]
    public async Task SearchBookPages_RanksDenselyMatchingPagesAboveEarlierOnes()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1",
            "A passing mention of aspekt here.",
            "Another passing mention of aspekt.",
            "Aspekt dokonany i aspekt niedokonany. Aspekt decyduje o znaczeniu, a aspekt jest kluczowy.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"aspekt"}""", context, CancellationToken.None));

        var matches = result.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(3, matches[0].GetProperty("page_number").GetInt32());
        Assert.Equal(4, matches[0].GetProperty("hits").GetInt32());
        Assert.Equal([3, 1, 2], matches.Select(match => match.GetProperty("page_number").GetInt32()));
    }

    // A miss has to teach the model what to try next, which is what keeps it searching
    // instead of announcing that the book does not cover the topic.
    [Fact]
    public async Task SearchBookPages_ReportsWhichTermFailedWhenNothingMatches()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1",
            "Odmiana czasownika w czasie przeszłym.",
            "Odmiana rzeczownika.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"odmiana gerund"}""", context, CancellationToken.None));

        Assert.Equal(0, result.GetProperty("match_count").GetInt32());
        var termPages = result.GetProperty("term_pages").EnumerateArray().ToList();
        Assert.Equal(2, termPages.Single(term => term.GetProperty("term").GetString() == "odmiana")
            .GetProperty("page_count").GetInt32());
        Assert.Equal(0, termPages.Single(term => term.GetProperty("term").GetString() == "gerund")
            .GetProperty("page_count").GetInt32());
        Assert.Contains("gerund", result.GetProperty("hint").GetString());
    }

    // A term that exists only before from_page must never be reported as absent: "this
    // word is nowhere in the book" is what would let the assistant tell a learner their
    // textbook does not cover something it covers on page 2.
    [Fact]
    public async Task SearchBookPages_CountsTermsOverTheWholeBookNotOnlyFromThePageSearched()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1",
            "Aspekt czasownika.",
            "Nic tutaj.",
            "Nic tutaj tez.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"aspekt","from_page":2}""", context, CancellationToken.None));

        Assert.Equal(0, result.GetProperty("match_count").GetInt32());
        var term = Assert.Single(result.GetProperty("term_pages").EnumerateArray());
        Assert.Equal(1, term.GetProperty("page_count").GetInt32());
        Assert.DoesNotContain("nowhere in the book", result.GetProperty("hint").GetString());
    }

    // Dropping the surplus keeps a long query useful, but the model has to be told which
    // words the AND it got was actually built from.
    [Fact]
    public async Task SearchBookPages_ReportsTermsDroppedBeyondTheCap()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1", "alfa beta gamma delta epsilon.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages",
            """{"query":"alfa beta gamma delta epsilon"}""",
            context,
            CancellationToken.None));

        Assert.Equal(4, result.GetProperty("terms").GetArrayLength());
        Assert.Equal(
            ["epsilon"],
            result.GetProperty("ignored_terms").EnumerateArray().Select(term => term.GetString()));
        Assert.Equal(1, result.GetProperty("match_count").GetInt32());
    }

    [Fact]
    public async Task SearchBookPages_SaysSoWhenTermsExistButNeverShareAPage()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1", "Tylko odmiana.", "Tylko czasownik.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"odmiana czasownik"}""", context, CancellationToken.None));

        Assert.Equal(0, result.GetProperty("match_count").GetInt32());
        Assert.Contains("never together", result.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task SearchBookPages_SaysTheBookNeverUsesAnyOfTheTerms()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1", "Odmiana czasownika.", "Odmiana rzeczownika.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"conjugation tense"}""", context, CancellationToken.None));

        Assert.Equal(0, result.GetProperty("match_count").GetInt32());
        Assert.Contains("another language", result.GetProperty("hint").GetString());
    }

    // The metrics that decide whether retrieval is working have to survive
    // AssistantAnalytics:CaptureContent being off, which is how production runs. These are
    // span tags rather than stored tool arguments precisely so they do not depend on it.
    [Fact]
    public async Task SearchBookPages_RecordsMatchCountsOnTheToolSpan()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1", "Aspekt i aspekt.", "Nic tutaj.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };
        using var listener = ListenToAssistantSpans(out var stopped);

        using (StartToolSpan())
        {
            await tools.ExecuteAsync(
                "search_book_pages", """{"query":"aspekt"}""", context, CancellationToken.None);
        }

        var span = Assert.Single(stopped);
        Assert.Equal(1, span.GetTagItem("assistant.search.term_count"));
        Assert.Equal(1, span.GetTagItem("assistant.search.match_count"));
        Assert.Equal(1, span.GetTagItem("assistant.search.returned_count"));
        Assert.Equal(0, span.GetTagItem("assistant.search.zero_page_terms"));
        Assert.Equal(2, span.GetTagItem("assistant.search.top_page_hits"));
    }

    [Fact]
    public async Task SearchBookPages_RecordsAMissAndWhichTermsWereAbsent()
    {
        await using var db = CreateContext();
        var bookId = await SeedPagesAsync(db, "user-1", "Odmiana czasownika.", "Odmiana rzeczownika.");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };
        using var listener = ListenToAssistantSpans(out var stopped);

        using (StartToolSpan())
        {
            await tools.ExecuteAsync(
                "search_book_pages", """{"query":"odmiana gerund"}""", context, CancellationToken.None);
        }

        var span = Assert.Single(stopped);
        Assert.Equal(2, span.GetTagItem("assistant.search.term_count"));
        Assert.Equal(0, span.GetTagItem("assistant.search.match_count"));
        Assert.Equal(1, span.GetTagItem("assistant.search.zero_page_terms"));
    }

    private static ActivityListener ListenToAssistantSpans(out List<Activity> stopped)
    {
        var captured = new List<Activity>();
        stopped = captured;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GenerativeAiTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>The span the turn runner has open while a tool executes.</summary>
    private static Activity? StartToolSpan() =>
        AssistantAnalyticsTelemetry.StartTool(Guid.NewGuid(), Guid.NewGuid(), "search_book_pages");

    [Fact]
    public async Task SearchBookPages_RejectsAnotherUsersBook()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "owner", pageCount: 2);
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "intruder", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"page"}""", context, CancellationToken.None));

        Assert.Equal("Book not found.", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SearchBookPages_NeedsAQuery()
    {
        await using var db = CreateContext();
        var bookId = await SeedBookAsync(db, "user-1", pageCount: 2);
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", BookDocumentId = bookId };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "search_book_pages", """{"query":"   "}""", context, CancellationToken.None));

        Assert.Equal("query is required.", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListBooks_ReturnsOnlyTheCurrentLanguagesBooks()
    {
        await using var db = CreateContext();
        await SeedBookAsync(db, "user-1", pageCount: 1, title: "Polish Reader", language: "Polish");
        await SeedBookAsync(db, "user-1", pageCount: 1, title: "German Reader", language: "German");
        await SeedBookAsync(db, "user-2", pageCount: 1, title: "Someone else's", language: "Polish");
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", CurrentLanguage = "Polish" };

        var result = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "list_books", "{}", context, CancellationToken.None));

        var book = Assert.Single(result.GetProperty("books").EnumerateArray());
        Assert.Equal("Polish Reader", book.GetProperty("title").GetString());
        Assert.Equal(1, result.GetProperty("total_count").GetInt32());
    }

    private static async Task<Guid> SeedBookAsync(
        GlosifyContext db,
        string userId,
        int pageCount,
        string title = "Polish Reader",
        string language = "Polish",
        string? pageText = null)
    {
        var bookId = Guid.NewGuid();
        db.BookDocuments.Add(new Glosify.Models.Library.BookDocument
        {
            Id = bookId,
            UserId = userId,
            Title = title,
            OriginalFileName = "reader.pdf",
            BlobName = $"books/{bookId}.pdf",
            Language = language,
            PageCount = pageCount,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            db.BookPages.Add(new Glosify.Models.Library.BookPage
            {
                Id = Guid.NewGuid(),
                BookDocumentId = bookId,
                PageNumber = pageNumber,
                Text = pageText ?? $"Page {pageNumber} text.",
            });
        }
        await db.SaveChangesAsync();
        return bookId;
    }

    /// <summary>Seeds a book whose pages have distinct text, one string per page.</summary>
    private static async Task<Guid> SeedPagesAsync(
        GlosifyContext db,
        string userId,
        params string[] pageTexts)
    {
        var bookId = await SeedBookAsync(db, userId, pageCount: pageTexts.Length);
        var pages = await db.BookPages
            .Where(page => page.BookDocumentId == bookId)
            .ToListAsync();
        foreach (var page in pages)
        {
            page.Text = pageTexts[page.PageNumber - 1];
        }
        await db.SaveChangesAsync();
        return bookId;
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static Quiz CreateQuiz(Guid id, string userId) => new()
    {
        Id = id,
        UserId = userId,
        Name = "Polish",
        SourceLanguage = "English",
        TargetLanguage = "Polish",
        Language = "Polish",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
