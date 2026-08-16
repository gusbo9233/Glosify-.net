using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class CustomQuizControllerTests
{
    [Fact]
    public async Task Create_returns_validation_problem_for_missing_document()
    {
        var controller = CreateController();
        var request = new SaveCustomQuizRequest
        {
            QuizId = Guid.NewGuid(),
            Name = "Invalid",
            Document = null!,
        };

        var result = await controller.Create(request, CancellationToken.None);

        var problem = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(400, Assert.IsType<ValidationProblemDetails>(problem.Value).Status);
    }

    [Fact]
    public async Task Grade_returns_validation_problem_for_null_answers()
    {
        var controller = CreateController();
        var request = new GradeCustomQuizRequest
        {
            Answers = null!,
        };

        var result = await controller.Grade(Guid.NewGuid(), request, CancellationToken.None);

        var problem = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(400, Assert.IsType<ValidationProblemDetails>(problem.Value).Status);
    }

    [Fact]
    public async Task Update_returns_validation_problem_when_model_binding_failed()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("$", "The JSON value could not be converted.");

        var result = await controller.Update(Guid.NewGuid(), null, CancellationToken.None);

        var problem = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(400, Assert.IsType<ValidationProblemDetails>(problem.Value).Status);
    }

    // The editor is only reachable through a saved custom quiz. An unsaved draft has no
    // id to give the assistant panel, and without one the assistant can only create a
    // second custom quiz instead of filling in the one on screen.
    [Fact]
    public async Task New_persists_the_starter_quiz_and_redirects_to_its_editor()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.New(quizId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CustomQuizController.Edit), redirect.ActionName);
        var created = Assert.Single(await db.CustomQuizzes.ToListAsync());
        Assert.Equal(created.Id, redirect.RouteValues!["id"]);
        Assert.Equal("Custom quiz", created.Name);
    }

    [Fact]
    public async Task New_gives_each_starter_quiz_a_free_name()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        await controller.New(quizId, CancellationToken.None);
        await controller.New(quizId, CancellationToken.None);

        var names = await db.CustomQuizzes.Select(item => item.Name).ToListAsync();
        Assert.Equal(["Custom quiz", "Custom quiz 2"], names.Order().ToList());
    }

    [Fact]
    public async Task New_returns_not_found_for_another_users_quiz()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var quiz = CreateQuiz(quizId);
        quiz.UserId = "someone-else";
        db.Quizzes.Add(quiz);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        Assert.IsType<NotFoundResult>(await controller.New(quizId, CancellationToken.None));
        Assert.Empty(await db.CustomQuizzes.ToListAsync());
    }

    private static CustomQuizController CreateController() =>
        new(null!, null!, null!, null!);

    private static CustomQuizController CreateController(GlosifyContext context)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test");
        return new CustomQuizController(
            new CustomQuizService(context),
            new CustomQuizTemplateCatalog(),
            new QuizService(context, new StaticLanguage(), new Glosify.Services.Anki.AnkiCollectionService(context, TimeProvider.System)),
            new WordService(context, new Glosify.Services.Anki.AnkiCollectionService(context, TimeProvider.System)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static Quiz CreateQuiz(Guid id) => new()
    {
        Id = id,
        UserId = UserId,
        Name = "Polish",
        SourceLanguage = "English",
        TargetLanguage = "Polish",
        Language = "Polish",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private const string UserId = "user-1";

    private sealed class StaticLanguage : ILanguageContext
    {
        public string? CurrentLanguage => "Polish";
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }
}
