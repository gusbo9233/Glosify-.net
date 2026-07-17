using System.Text.Json;
using Glosify.Filters;
using Glosify.Services.Ai.Generation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AiServiceExceptionFilterTests
{
    [Theory]
    [MemberData(nameof(GenerativeAiErrors))]
    public void Maps_provider_neutral_errors_to_sanitized_statuses(
        Exception exception,
        int expectedStatus,
        string expectedMessage)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor { DisplayName = "AI test action" },
            new ModelStateDictionary());
        var exceptionContext = new ExceptionContext(actionContext, [])
        {
            Exception = exception,
        };

        new AiServiceExceptionFilterAttribute().OnException(exceptionContext);

        var result = Assert.IsType<ObjectResult>(exceptionContext.Result);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(
            expectedMessage,
            JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString());
        Assert.True(exceptionContext.ExceptionHandled);
    }

    public static TheoryData<Exception, int, string> GenerativeAiErrors => new()
    {
        {
            new GenerativeAiValidationException("Invalid image."),
            StatusCodes.Status400BadRequest,
            "Invalid image."
        },
        {
            new GenerativeAiDependencyUnavailableException("Temporarily unavailable."),
            StatusCodes.Status503ServiceUnavailable,
            "Temporarily unavailable."
        },
        {
            new GenerativeAiTimeoutException("Timed out."),
            StatusCodes.Status504GatewayTimeout,
            "Timed out."
        },
        {
            new GenerativeAiUpstreamException("Upstream failed."),
            StatusCodes.Status502BadGateway,
            "Upstream failed."
        },
    };
}
