using Glosify.Controllers;
using Microsoft.AspNetCore.Mvc;
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

    private static CustomQuizController CreateController() =>
        new(null!, null!, null!, null!);
}
