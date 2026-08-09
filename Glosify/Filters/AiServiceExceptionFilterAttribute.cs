using Glosify.Services;
using Glosify.Infrastructure.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Glosify.Filters;

/// <summary>
/// Maps the exceptions shared by AI-backed endpoints onto their HTTP statuses so
/// controllers don't repeat the same catch ladder: insufficient credits → 402,
/// exhausted application budget → 503, unknown quiz → 404, foreign resource → 403,
/// dependency warm-up → 503, and any other failure → 500. All bodies use the
/// application's Problem Details contract.
/// Apply to actions or controllers that call AI services and return JSON;
/// exceptions an action catches itself never reach this filter.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AiServiceExceptionFilterAttribute : ExceptionFilterAttribute
{
    private const string UnexpectedErrorMessage = "The assistant hit an unexpected error. Please try again.";

    public override void OnException(ExceptionContext context)
    {
        var exception = context.Exception;

        // A cancelled request has no reader; let the abort propagate instead of
        // mislabelling it a warm-up failure (provider timeouts can also surface
        // as TaskCanceledException, so only genuine client aborts are excluded).
        if (exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var error = ApiExceptionMapper.Map(exception);
        if (error is not null)
        {
            context.Result = Error(context, error.Value);
        }
        else if (ServiceWarmupMessage.IsDatabaseWarmupFailure(exception))
        {
            context.Result = Warmup(context, ServiceWarmupMessage.Dependencies);
        }
        else if (ServiceWarmupMessage.IsLlmWarmupFailure(exception))
        {
            context.Result = Warmup(context, ServiceWarmupMessage.LlmAssistant);
        }
        else
        {
            context.Result = Unexpected(context);
        }

        context.ExceptionHandled = true;
    }

    private static Microsoft.AspNetCore.Mvc.ObjectResult Error(ExceptionContext context, ApiError error) =>
        GlosifyProblemDetails.Result(
            context.HttpContext,
            error.StatusCode,
            error.Code,
            error.Detail);

    private static ObjectResult Warmup(ExceptionContext context, string message)
    {
        GetLogger(context).LogWarning(
            context.Exception,
            "Dependency warm-up interrupted {Action}",
            context.ActionDescriptor.DisplayName);
        return GlosifyProblemDetails.Result(
            context.HttpContext,
            StatusCodes.Status503ServiceUnavailable,
            ApiErrorCodes.DependencyUnavailable,
            message);
    }

    private static ObjectResult Unexpected(ExceptionContext context)
    {
        GetLogger(context).LogError(
            context.Exception,
            "AI-backed action {Action} failed",
            context.ActionDescriptor.DisplayName);
        return GlosifyProblemDetails.Result(
            context.HttpContext,
            StatusCodes.Status500InternalServerError,
            ApiErrorCodes.Unexpected,
            UnexpectedErrorMessage);
    }

    private static ILogger GetLogger(ExceptionContext context)
        => context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<AiServiceExceptionFilterAttribute>();
}
