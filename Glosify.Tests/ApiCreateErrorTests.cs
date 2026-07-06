using Glosify.Models.Api;
using System.Security.Claims;
using Glosify.Controllers.Api;
using Glosify.Data;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Glosify.Services.Ai;
using Glosify.Services.Language;
using Glosify.Services.Typing;
using Glosify.Services.Words;

namespace Glosify.Tests;

public class ApiCreateErrorTests
{
    [Fact]
    public async Task CreateQuiz_ReturnsBadRequestForForeignCollection()
    {
        await using var context = CreateContext();
        context.Collections.Add(new Collection
        {
            Id = Guid.NewGuid(),
            UserId = "someone-else",
            Name = "Not yours",
            Language = "Polish",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        var foreignCollectionId = context.Collections.Single().Id;
        var controller = CreateQuizzesController(context);

        var result = await controller.Create(new CreateQuizRequest(
            "Basics", "English", "Polish", foreignCollectionId));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateCollection_ReturnsConflictForDuplicateName()
    {
        await using var context = CreateContext();
        context.Collections.Add(new Collection
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = "Food",
            Language = "Polish",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        var controller = CreateCollectionsController(context);

        var result = await controller.Create(new CreateCollectionRequest("Food", "Polish", null));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateCollection_ReturnsBadRequestForUnknownParent()
    {
        await using var context = CreateContext();
        var controller = CreateCollectionsController(context);

        var result = await controller.Create(new CreateCollectionRequest("Food", "Polish", Guid.NewGuid()));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private const string UserId = "user-1";

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static QuizzesApiController CreateQuizzesController(GlosifyContext context)
    {
        var controller = new QuizzesApiController(
            new QuizService(context, new StaticLanguage()),
            new WordService(context),
            new TypingQuizService(context),
            new StubQuizRepairService(),
            new StubImageTextExtractionService());
        controller.ControllerContext = CreateControllerContext();
        return controller;
    }

    private static CollectionsApiController CreateCollectionsController(GlosifyContext context)
    {
        var controller = new CollectionsApiController(new CollectionService(context));
        controller.ControllerContext = CreateControllerContext();
        return controller;
    }

    private static ControllerContext CreateControllerContext()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private sealed class StubQuizRepairService : IQuizRepairService
    {
        public Task<QuizRepairResult> RepairWordAsync(string wordId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult(new QuizRepairResult(QuizRepairStatus.NotFound));

        public Task<QuizRepairResult> RepairSentenceAsync(Guid quizId, string sentenceText, string userId, CancellationToken cancellationToken) =>
            Task.FromResult(new QuizRepairResult(QuizRepairStatus.NotFound));
    }

    private sealed class StubImageTextExtractionService : IImageTextExtractionService
    {
        public Task<string> ExtractTextAsync(string userId, Stream imageStream, string contentType, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class StaticLanguage : ILanguageContext
    {
        public string? CurrentLanguage => "Polish";
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }
}
