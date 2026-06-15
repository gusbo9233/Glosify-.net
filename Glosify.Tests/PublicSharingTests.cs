using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class PublicSharingTests
{
    [Fact]
    public async Task SetQuizPublic_OnlyUpdatesOwnedQuiz()
    {
        await using var context = CreateContext();
        var quiz = CreateQuiz("owner", "Polish");
        context.Quizzes.Add(quiz);
        await context.SaveChangesAsync();
        var service = new QuizService(context, new TestLanguageContext("Polish"));

        Assert.False(await service.SetQuizPublicAsync(quiz.Id, "other-user", true));
        Assert.False((await context.Quizzes.SingleAsync(q => q.Id == quiz.Id)).IsPublic);

        Assert.True(await service.SetQuizPublicAsync(quiz.Id, "owner", true));
        Assert.True((await context.Quizzes.SingleAsync(q => q.Id == quiz.Id)).IsPublic);
    }

    [Fact]
    public async Task PublicCollectionRoots_ExcludesPublicChildrenOfPublicParents()
    {
        await using var context = CreateContext();
        var publicRoot = CreateCollection("owner", "Root", "Polish", isPublic: true);
        var publicChild = CreateCollection("owner", "Child", "Polish", publicRoot.Id, isPublic: true);
        var privateRoot = CreateCollection("owner", "Private Root", "Polish");
        var independentPublicChild = CreateCollection("owner", "Independent Child", "Polish", privateRoot.Id, isPublic: true);
        context.Collections.AddRange(publicRoot, publicChild, privateRoot, independentPublicChild);
        await context.SaveChangesAsync();
        var service = new CollectionService(context);

        var roots = await service.GetPublicCollectionRootsAsync("Polish");

        Assert.Contains(roots, c => c.Id == publicRoot.Id);
        Assert.Contains(roots, c => c.Id == independentPublicChild.Id);
        Assert.DoesNotContain(roots, c => c.Id == publicChild.Id);
        Assert.DoesNotContain(roots, c => c.Id == privateRoot.Id);
    }

    [Fact]
    public async Task PublicQuizListing_ExcludesQuizzesInsidePublicCollectionTrees()
    {
        await using var context = CreateContext();
        var publicCollection = CreateCollection("owner", "Shared", "Polish", isPublic: true);
        var collectionQuiz = CreateQuiz("owner", "Polish", publicCollection.Id, isPublic: true);
        var standaloneQuiz = CreateQuiz("owner", "Polish", isPublic: true);
        context.Collections.Add(publicCollection);
        context.Quizzes.AddRange(collectionQuiz, standaloneQuiz);
        await context.SaveChangesAsync();
        var service = new QuizService(context, new TestLanguageContext("Polish"));

        var quizzes = await service.GetPublicQuizzesAsync("Polish");

        var quiz = Assert.Single(quizzes);
        Assert.Equal(standaloneQuiz.Id, quiz.Id);
    }

    [Fact]
    public async Task CopyPublicQuiz_CopiesWordsAndSentencesAsPrivateOwnedQuiz()
    {
        await using var context = CreateContext();
        var source = CreateQuiz("owner", "Polish", isPublic: true);
        context.Quizzes.Add(source);
        context.Words.Add(new Word { Id = "word-1", QuizId = source.Id, Lemma = "dom", Translation = "house" });
        context.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = source.Id,
            Text = "To jest dom.",
            Translation = "This is a house.",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        var service = new QuizService(context, new TestLanguageContext("Polish"));

        var copy = await service.CopyPublicQuizAsync(source.Id, "learner");

        Assert.NotNull(copy);
        Assert.Equal("learner", copy!.UserId);
        Assert.False(copy.IsPublic);
        Assert.Equal(source.Id, copy.OriginalQuizId);
        Assert.Equal("dom", (await context.Words.SingleAsync(w => w.QuizId == copy.Id)).Lemma);
        Assert.Equal("To jest dom.", (await context.QuizSentences.SingleAsync(s => s.QuizId == copy.Id)).Text);
    }

    [Fact]
    public async Task CopyPrivateQuiz_ReturnsNull()
    {
        await using var context = CreateContext();
        var source = CreateQuiz("owner", "Polish");
        context.Quizzes.Add(source);
        await context.SaveChangesAsync();
        var service = new QuizService(context, new TestLanguageContext("Polish"));

        var copy = await service.CopyPublicQuizAsync(source.Id, "learner");

        Assert.Null(copy);
    }

    [Fact]
    public async Task CopyPublicCollection_PreservesTreeAndCopiesContentPrivately()
    {
        await using var context = CreateContext();
        var root = CreateCollection("owner", "Root", "Polish", isPublic: true);
        var child = CreateCollection("owner", "Child", "Polish", root.Id);
        var rootQuiz = CreateQuiz("owner", "Polish", root.Id);
        var childQuiz = CreateQuiz("owner", "Polish", child.Id);
        context.Collections.AddRange(root, child);
        context.Quizzes.AddRange(rootQuiz, childQuiz);
        context.Words.AddRange(
            new Word { Id = "root-word", QuizId = rootQuiz.Id, Lemma = "kot", Translation = "cat" },
            new Word { Id = "child-word", QuizId = childQuiz.Id, Lemma = "pies", Translation = "dog" });
        context.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = childQuiz.Id,
            Text = "To jest pies.",
            Translation = "This is a dog.",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        var service = new CollectionService(context);

        var copiedRoot = await service.CopyPublicCollectionAsync(root.Id, "learner");

        Assert.NotNull(copiedRoot);
        var copiedCollections = await context.Collections
            .Where(c => c.UserId == "learner")
            .OrderBy(c => c.Name)
            .ToListAsync();
        Assert.Equal(2, copiedCollections.Count);
        Assert.All(copiedCollections, c => Assert.False(c.IsPublic));

        var copiedChild = Assert.Single(copiedCollections, c => c.Name == "Child");
        Assert.Equal(copiedRoot!.Id, copiedChild.ParentCollectionId);
        Assert.Equal(child.Id, copiedChild.OriginalCollectionId);

        var copiedQuizzes = await context.Quizzes
            .Where(q => q.UserId == "learner")
            .ToListAsync();
        Assert.Equal(2, copiedQuizzes.Count);
        Assert.All(copiedQuizzes, q => Assert.False(q.IsPublic));
        Assert.Equal(2, await context.Words.CountAsync(w => copiedQuizzes.Select(q => q.Id).Contains(w.QuizId)));
        Assert.Single(await context.QuizSentences.Where(s => copiedQuizzes.Select(q => q.Id).Contains(s.QuizId)).ToListAsync());
    }

    [Fact]
    public async Task PublicCollectionTree_ReadsInheritedPublicDescendant()
    {
        await using var context = CreateContext();
        var root = CreateCollection("owner", "Root", "Polish", isPublic: true);
        var child = CreateCollection("owner", "Child", "Polish", root.Id);
        context.Collections.AddRange(root, child);
        await context.SaveChangesAsync();
        var service = new CollectionService(context);

        var tree = await service.GetPublicCollectionTreeAsync(child.Id);

        Assert.NotNull(tree);
        Assert.Equal(child.Id, tree!.Id);
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static Quiz CreateQuiz(
        string userId,
        string language,
        Guid? collectionId = null,
        bool isPublic = false) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Shared quiz",
            UserId = userId,
            CollectionId = collectionId,
            SourceLanguage = "English",
            TargetLanguage = language,
            Language = language,
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = "Ready",
            IsPublic = isPublic
        };

    private static Collection CreateCollection(
        string userId,
        string name,
        string language,
        Guid? parentCollectionId = null,
        bool isPublic = false) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Language = language,
            ParentCollectionId = parentCollectionId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsPublic = isPublic
        };

    private sealed class TestLanguageContext : ILanguageContext
    {
        public TestLanguageContext(string? currentLanguage)
        {
            CurrentLanguage = currentLanguage;
        }

        public string? CurrentLanguage { get; }
        public bool HasLanguage => CurrentLanguage != null;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];
        public bool TrySetLanguage(string language) => false;
        public void Clear()
        {
        }
    }
}
