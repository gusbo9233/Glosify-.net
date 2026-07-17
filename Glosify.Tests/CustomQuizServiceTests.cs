using System.Text.Json;
using Glosify.Data;
using Glosify.Models.CustomQuizzes;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Words;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class CustomQuizServiceTests
{
    private const string OwnerId = "owner";

    [Fact]
    public async Task ListForQuizzes_loads_playable_summaries_for_all_requested_quizzes()
    {
        await using var db = CreateContext();
        var (firstQuiz, firstWord) = await SeedQuizAsync(db);
        var secondQuiz = new Quiz
        {
            Id = Guid.NewGuid(),
            UserId = OwnerId,
            Name = "German",
            SourceLanguage = "en",
            TargetLanguage = "de",
            Language = "de",
        };
        var secondWord = new Word
        {
            Id = "second-word",
            QuizId = secondQuiz.Id,
            Lemma = "Haus",
            Translation = "house",
        };
        db.AddRange(secondQuiz, secondWord);
        await db.SaveChangesAsync();
        var service = new CustomQuizService(db);
        await service.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = firstQuiz.Id,
            Name = "First playable",
            Document = TextQuiz(firstWord.Id),
        }, OwnerId);
        await service.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = firstQuiz.Id,
            Name = "Draft",
            Document = new(),
        }, OwnerId);
        await service.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = secondQuiz.Id,
            Name = "Second playable",
            Document = TextQuiz(secondWord.Id),
        }, OwnerId);

        var grouped = await service.ListForQuizzesAsync(
            [firstQuiz.Id, secondQuiz.Id, firstQuiz.Id],
            playableOnly: true);

        Assert.Equal(["First playable"], grouped[firstQuiz.Id].Select(item => item.Name));
        Assert.Equal(["Second playable"], grouped[secondQuiz.Id].Select(item => item.Name));
    }

    [Fact]
    public async Task Create_AllowsDraft_AndMarksCompleteDocumentPlayable()
    {
        await using var db = CreateContext();
        var (quiz, word) = await SeedQuizAsync(db);
        var service = new CustomQuizService(db);

        var draft = await service.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = quiz.Id,
            Name = "Draft",
            Document = new CustomQuizDocumentV1()
        }, OwnerId);
        var playable = await service.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = quiz.Id,
            Name = "Coffee",
            Document = TextQuiz(word.Id)
        }, OwnerId);

        Assert.False(draft.IsPlayable);
        Assert.Contains(draft.PlayabilityErrors, error => error.Contains("answer control", StringComparison.OrdinalIgnoreCase));
        Assert.True(playable.IsPlayable);
    }

    [Fact]
    public async Task GetForEditor_DefaultsMissingAnswerLabels_AndRepairsStoredPlayability()
    {
        await using var db = CreateContext();
        var (quiz, _) = await SeedQuizAsync(db, "być", "to be");
        var customQuizId = Guid.NewGuid();
        var document = new CustomQuizDocumentV1
        {
            Blocks =
            [
                new() { Id = "pres_1s", Type = CustomQuizBlockTypes.TextInput, Text = "będ... (I will be)", ExpectedText = "ę", ColumnSpan = 6 },
                new() { Id = "submit", Type = CustomQuizBlockTypes.SubmitButton, ColumnSpan = 6 },
                new() { Id = "feedback", Type = CustomQuizBlockTypes.FeedbackMessage, ColumnSpan = 6 },
            ]
        };
        db.CustomQuizzes.Add(new CustomQuiz
        {
            Id = customQuizId,
            QuizId = quiz.Id,
            Name = "Generated endings",
            DefinitionJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            SchemaVersion = 1,
            IsPlayable = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var editor = await new CustomQuizService(db).GetForEditorAsync(customQuizId, OwnerId);

        Assert.True(editor!.IsPlayable);
        Assert.Equal("będ... (I will be)", editor.Document.Blocks.Single(block => block.Id == "pres_1s").Label);
        Assert.True((await db.CustomQuizzes.SingleAsync(item => item.Id == customQuizId)).IsPlayable);
    }

    [Fact]
    public async Task Create_UpgradesLegacyLayout_AndMovesOverlappingBlocksToNearestOpenCells()
    {
        await using var db = CreateContext();
        var (quiz, word) = await SeedQuizAsync(db);
        var document = TextQuiz(word.Id);
        foreach (var block in document.Blocks)
        {
            block.GridColumn = 0;
            block.GridRow = 0;
            block.ColumnSpan = 6;
        }

        var created = await new CustomQuizService(db).CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = quiz.Id,
            Name = "Positioned",
            Document = document
        }, OwnerId);

        Assert.All(created.Document.Blocks, block =>
        {
            Assert.InRange(block.GridColumn, 1, 7);
            Assert.InRange(block.GridRow, 1, 500);
        });
        Assert.DoesNotContain(created.Document.Blocks.GroupBy(block => block.GridRow), row =>
            row.OrderBy(block => block.GridColumn)
                .Zip(row.OrderBy(block => block.GridColumn).Skip(1))
                .Any(pair => pair.Second.GridColumn < pair.First.GridColumn + pair.First.ColumnSpan));
    }

    [Fact]
    public async Task Create_RejectsBindingToForeignWord()
    {
        await using var db = CreateContext();
        var (quiz, _) = await SeedQuizAsync(db);
        var foreignQuiz = new Quiz { Id = Guid.NewGuid(), UserId = OwnerId, Name = "Other", SourceLanguage = "en", TargetLanguage = "pl", Language = "pl" };
        var foreignWord = new Word { Id = "foreign", QuizId = foreignQuiz.Id, Lemma = "obcy", Translation = "foreign" };
        db.AddRange(foreignQuiz, foreignWord);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<CustomQuizValidationException>(() => new CustomQuizService(db).CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = quiz.Id, Name = "Invalid", Document = TextQuiz(foreignWord.Id)
        }, OwnerId));

        Assert.Contains(exception.Errors, error => error.Contains("outside this quiz", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateAndDelete_RequireOwner_RejectDuplicateName_AndDetectStaleRowVersion()
    {
        await using var db = CreateContext();
        var (quiz, word) = await SeedQuizAsync(db);
        var service = new CustomQuizService(db);
        var first = await service.CreateAsync(new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "First", Document = TextQuiz(word.Id) }, OwnerId);
        await service.CreateAsync(new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "Second", Document = TextQuiz(word.Id) }, OwnerId);

        Assert.Null(await service.UpdateAsync(first.Id!.Value, new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "Changed", Document = TextQuiz(word.Id) }, "someone-else"));
        Assert.False(await service.DeleteAsync(first.Id.Value, "someone-else"));
        await Assert.ThrowsAsync<CustomQuizValidationException>(() => service.UpdateAsync(first.Id.Value,
            new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "Second", Document = TextQuiz(word.Id) }, OwnerId));

        var tracked = await db.CustomQuizzes.SingleAsync(item => item.Id == first.Id.Value);
        tracked.RowVersion = [1, 2, 3];
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<CustomQuizConcurrencyException>(() => service.UpdateAsync(first.Id.Value,
            new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "First", Document = TextQuiz(word.Id), RowVersion = Convert.ToBase64String([9, 9, 9]) }, OwnerId));
    }

    [Fact]
    public async Task Grade_TrimsAndIgnoresCase_ButPreservesAccents_AndIsIdempotent()
    {
        await using var db = CreateContext();
        var (quiz, word) = await SeedQuizAsync(db, "Żółć", "bile");
        var service = new CustomQuizService(db);
        var created = await service.CreateAsync(new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "Accents", Document = TextQuiz(word.Id) }, OwnerId);
        var attemptId = Guid.NewGuid();

        var wrong = await service.GradeAsync(created.Id!.Value, new GradeCustomQuizRequest
        {
            AttemptId = attemptId,
            Answers = [new() { BlockId = "answer", Values = ["zolc"] }]
        }, OwnerId);
        var repeated = await service.GradeAsync(created.Id.Value, new GradeCustomQuizRequest
        {
            AttemptId = attemptId,
            Answers = [new() { BlockId = "answer", Values = ["ŻÓŁĆ"] }]
        }, OwnerId);

        Assert.Equal("incorrect", wrong!.State);
        Assert.Equal("incorrect", repeated!.State);
        Assert.Single(db.QuizAttempts);

        var correct = await service.GradeAsync(created.Id.Value, new GradeCustomQuizRequest
        {
            AttemptId = Guid.NewGuid(),
            Answers = [new() { BlockId = "answer", Values = ["  żÓłĆ  "] }]
        }, OwnerId);
        Assert.Equal("correct", correct!.State);
    }

    [Fact]
    public async Task Grade_DoesNotRecordIncompleteSubmission_AndPlayerHidesAnswerMetadata()
    {
        await using var db = CreateContext();
        var (quiz, word) = await SeedQuizAsync(db);
        var service = new CustomQuizService(db);
        var created = await service.CreateAsync(new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "Coffee", Document = TextQuiz(word.Id) }, OwnerId);

        var play = await service.GetForPlayAsync(created.Id!.Value, OwnerId);
        var result = await service.GradeAsync(created.Id.Value, new GradeCustomQuizRequest { AttemptId = Guid.NewGuid() }, OwnerId);

        Assert.NotNull(play);
        Assert.Null(play!.Document.Blocks.Single(block => block.Id == "answer").ExpectedBinding);
        Assert.Equal("incomplete", result!.State);
        Assert.Empty(db.QuizAttempts);
    }

    [Fact]
    public async Task Grade_SupportsLiteralExpectedText_ForFillInEndings()
    {
        await using var db = CreateContext();
        var (quiz, _) = await SeedQuizAsync(db, "być", "to be");
        var service = new CustomQuizService(db);
        var document = new CustomQuizDocumentV1
        {
            Blocks =
            [
                new() { Id = "instruction", Type = CustomQuizBlockTypes.InstructionLabel, Text = "Complete: będ___ (I will be)" },
                new() { Id = "ending", Type = CustomQuizBlockTypes.TextInput, Label = "Verb ending", ExpectedText = "ę" },
                new() { Id = "submit", Type = CustomQuizBlockTypes.SubmitButton },
                new() { Id = "feedback", Type = CustomQuizBlockTypes.FeedbackMessage },
            ]
        };
        var created = await service.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = quiz.Id,
            Name = "Verb endings",
            Document = document,
        }, OwnerId);

        var play = await service.GetForPlayAsync(created.Id!.Value, OwnerId);
        var grade = await service.GradeAsync(created.Id.Value, new GradeCustomQuizRequest
        {
            AttemptId = Guid.NewGuid(),
            Answers = [new() { BlockId = "ending", Values = ["Ę"] }],
        }, OwnerId);

        Assert.True(created.IsPlayable);
        Assert.Null(play!.Document.Blocks.Single(block => block.Id == "ending").ExpectedText);
        Assert.Equal("correct", grade!.State);
    }

    [Fact]
    public async Task Grade_SupportsEveryV1AnswerControl()
    {
        await using var db = CreateContext();
        var (quiz, coffee) = await SeedQuizAsync(db);
        var tea = new Word { Id = "tea", QuizId = quiz.Id, Lemma = "herbata", Translation = "tea" };
        db.Words.Add(tea);
        await db.SaveChangesAsync();
        var document = new CustomQuizDocumentV1
        {
            Blocks =
            [
                new() { Id = "text", Type = CustomQuizBlockTypes.TextInput, Order = 0, ColumnSpan = 6, Label = "Text", ExpectedBinding = Bind(coffee.Id) },
                new() { Id = "area", Type = CustomQuizBlockTypes.Textarea, Order = 1, ColumnSpan = 6, Label = "Area", ExpectedBinding = Bind(tea.Id) },
                new() { Id = "check", Type = CustomQuizBlockTypes.Checkbox, Order = 2, ColumnSpan = 3, Label = "Check", Binding = Bind(coffee.Id), ExpectedChecked = true },
                Choice("radio", CustomQuizBlockTypes.RadioGroup, 3, coffee.Id, tea.Id),
                new() { Id = "multi", Type = CustomQuizBlockTypes.MultiSelectGroup, Order = 4, ColumnSpan = 6, Label = "Multi", Options = [Option("m1", coffee.Id, true), Option("m2", tea.Id, true)] },
                Choice("select", CustomQuizBlockTypes.SelectMenu, 5, coffee.Id, tea.Id),
                new() { Id = "submit", Type = CustomQuizBlockTypes.SubmitButton, Order = 6, ColumnSpan = 6 },
                new() { Id = "feedback", Type = CustomQuizBlockTypes.FeedbackMessage, Order = 7, ColumnSpan = 6 }
            ]
        };
        var service = new CustomQuizService(db);
        var created = await service.CreateAsync(new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "All controls", Document = document }, OwnerId);

        var grade = await service.GradeAsync(created.Id!.Value, new GradeCustomQuizRequest
        {
            AttemptId = Guid.NewGuid(),
            Answers =
            [
                new() { BlockId = "text", Values = ["KAWA"] },
                new() { BlockId = "area", Values = ["herbata"] },
                new() { BlockId = "check", Values = ["true"] },
                new() { BlockId = "radio", Values = ["radio-correct"] },
                new() { BlockId = "multi", Values = ["m2", "m1"] },
                new() { BlockId = "select", Values = ["select-correct"] }
            ]
        }, OwnerId);

        Assert.Equal("correct", grade!.State);
        Assert.Equal(6, grade.CorrectCount);
    }

    [Fact]
    public async Task Play_InheritsPublicAndClassroomAccess_ButDraftsRemainPrivate()
    {
        await using var db = CreateContext();
        var (quiz, word) = await SeedQuizAsync(db);
        var service = new CustomQuizService(db);
        var playable = await service.CreateAsync(new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "Playable", Document = TextQuiz(word.Id) }, OwnerId);
        var draft = await service.CreateAsync(new SaveCustomQuizRequest { QuizId = quiz.Id, Name = "Draft", Document = new() }, OwnerId);

        Assert.Null(await service.GetForPlayAsync(playable.Id!.Value, "learner"));
        quiz.IsPublic = true;
        await db.SaveChangesAsync();
        Assert.NotNull(await service.GetForPlayAsync(playable.Id.Value, "learner"));
        Assert.Null(await service.GetForPlayAsync(draft.Id!.Value, "learner"));

        quiz.IsPublic = false;
        var classroomId = Guid.NewGuid();
        db.ClassroomMemberships.Add(new ClassroomMembership { Id = Guid.NewGuid(), ClassroomId = classroomId, UserId = "learner" });
        db.ClassroomContents.Add(new ClassroomContent { Id = Guid.NewGuid(), ClassroomId = classroomId, QuizId = quiz.Id, SharedByUserId = OwnerId, ContentType = ClassroomContentType.Quiz });
        await db.SaveChangesAsync();
        Assert.NotNull(await service.GetForPlayAsync(playable.Id.Value, "learner", classroomId));
    }

    [Fact]
    public async Task Clone_RemapsBindings_AndDeleteWordPrunesBoundBlocks()
    {
        await using var db = CreateContext();
        var (source, sourceWord) = await SeedQuizAsync(db);
        var target = new Quiz { Id = Guid.NewGuid(), UserId = OwnerId, Name = "Copy", SourceLanguage = "en", TargetLanguage = "pl", Language = "pl" };
        var targetWord = new Word { Id = "copied", QuizId = target.Id, Lemma = sourceWord.Lemma, Translation = sourceWord.Translation };
        db.AddRange(target, targetWord);
        await db.SaveChangesAsync();
        var service = new CustomQuizService(db);
        await service.CreateAsync(new SaveCustomQuizRequest { QuizId = source.Id, Name = "Coffee", Document = TextQuiz(sourceWord.Id) }, OwnerId);

        await service.CloneForCopiedQuizAsync(source.Id, target.Id, new Dictionary<string, string> { [sourceWord.Id] = targetWord.Id });
        await db.SaveChangesAsync();
        var copiedSummary = Assert.Single(await service.ListForQuizAsync(target.Id));
        var copied = await service.GetForEditorAsync(copiedSummary.Id, OwnerId);
        Assert.Equal(targetWord.Id, copied!.Document.Blocks.Single(block => block.Id == "answer").ExpectedBinding!.WordId);

        await new WordService(db).DeleteWordAsync(targetWord.Id, OwnerId);
        copied = await service.GetForEditorAsync(copiedSummary.Id, OwnerId);
        Assert.DoesNotContain(copied!.Document.Blocks, block => block.Id is "prompt" or "answer");
        Assert.False(copied.IsPlayable);
    }

    [Fact]
    public async Task TemplateCatalog_BuildsDistinctPlayableDocumentsFromQuizWords()
    {
        await using var db = CreateContext();
        var (quiz, first) = await SeedQuizAsync(db);
        var words = new List<Word> { first };
        foreach (var (id, lemma, translation) in new[]
        {
            ("tea", "herbata", "tea"), ("milk", "mleko", "milk"), ("bread", "chleb", "bread")
        })
        {
            words.Add(new Word { Id = id, QuizId = quiz.Id, Lemma = lemma, Translation = translation });
        }
        db.Words.AddRange(words.Skip(1));
        await db.SaveChangesAsync();

        var templates = new CustomQuizTemplateCatalog().Build(words);
        var service = new CustomQuizService(db);
        var wordMap = words.ToDictionary(word => word.Id);

        Assert.Equal(4, templates.Count);
        Assert.Equal(4, templates.Select(template => template.StylePreset).Distinct().Count());
        var textbook = templates.Single(template => template.Id == "paper_choices");
        Assert.Equal("Textbook drill", textbook.Name);
        Assert.All(textbook.Document.Blocks.Where(block => block.Type == CustomQuizBlockTypes.TextInput), block =>
        {
            Assert.Contains("{{blank}}", block.Label);
            Assert.DoesNotContain("___", block.Label);
        });
        Assert.All(templates, template =>
        {
            var validation = service.Validate(template.Document, wordMap);
            Assert.True(validation.IsStructurallyValid, string.Join(" ", validation.StructuralErrors));
            Assert.True(validation.IsPlayable, string.Join(" ", validation.PlayabilityErrors));
        });
    }

    private static CustomQuizDocumentV1 TextQuiz(string wordId) => new()
    {
        Blocks =
        [
            new() { Id = "prompt", Type = CustomQuizBlockTypes.PromptLabel, Order = 0, ColumnSpan = 12, Binding = new() { WordId = wordId, Field = CustomQuizWordFields.Translation } },
            new() { Id = "answer", Type = CustomQuizBlockTypes.TextInput, Order = 1, ColumnSpan = 12, Label = "Polish answer", ExpectedBinding = new() { WordId = wordId, Field = CustomQuizWordFields.Lemma } },
            new() { Id = "submit", Type = CustomQuizBlockTypes.SubmitButton, Order = 2, ColumnSpan = 6, Text = "Check" },
            new() { Id = "feedback", Type = CustomQuizBlockTypes.FeedbackMessage, Order = 3, ColumnSpan = 6 }
        ]
    };

    private static CustomQuizBlockV1 Choice(string id, string type, int order, string correctWordId, string otherWordId) => new()
    {
        Id = id, Type = type, Order = order, ColumnSpan = 6, Label = $"Choose ({id})",
        Options = [Option($"{id}-correct", correctWordId, true), Option($"{id}-other", otherWordId, false)]
    };

    private static CustomQuizOptionV1 Option(string id, string wordId, bool correct) => new()
    {
        Id = id, Binding = Bind(wordId), IsCorrect = correct
    };

    private static CustomQuizWordBindingV1 Bind(string wordId) => new() { WordId = wordId, Field = CustomQuizWordFields.Lemma };

    private static async Task<(Quiz Quiz, Word Word)> SeedQuizAsync(GlosifyContext db, string lemma = "kawa", string translation = "coffee")
    {
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = OwnerId, Name = "Polish", SourceLanguage = "en", TargetLanguage = "pl", Language = "pl" };
        var word = new Word { Id = Guid.NewGuid().ToString("N"), QuizId = quiz.Id, Lemma = lemma, Translation = translation };
        db.AddRange(quiz, word);
        await db.SaveChangesAsync();
        return (quiz, word);
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }
}
