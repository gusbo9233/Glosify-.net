using Glosify.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Glosify.Services.Ai;
using Glosify.Services.Quizzes;
using Glosify.Services.Speaking;

namespace Glosify.Filters;

/// <summary>
/// Maps the exceptions shared by AI-backed endpoints onto their HTTP statuses so
/// controllers don't repeat the same catch ladder: insufficient credits → 402,
/// unknown quiz → 404, foreign resource → 403, dependency warm-up → 503, and any
/// other failure → 500. All bodies use the <c>{ "error": message }</c> shape.
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
        // mislabelling it a warm-up failure (Gemini timeouts also surface as
        // TaskCanceledException, so only genuine client aborts are excluded).
        if (exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        context.Result = exception switch
        {
            InsufficientAiCreditsException => Error(StatusCodes.Status402PaymentRequired, exception.Message),
            SpeakingValidationException => Error(StatusCodes.Status400BadRequest, exception.Message),
            SpeakingSessionNotFoundException => Error(StatusCodes.Status404NotFound, exception.Message),
            SpeakingSessionExpiredException => Error(StatusCodes.Status410Gone, exception.Message),
            SpeakingSessionBusyException => Error(StatusCodes.Status409Conflict, exception.Message),
            SpeakingSessionLimitException => Error(StatusCodes.Status409Conflict, exception.Message),
            SpeakingDependencyUnavailableException => Error(StatusCodes.Status503ServiceUnavailable, exception.Message),
            SpeakingUpstreamException => Error(StatusCodes.Status502BadGateway, exception.Message),
            QuizNotFoundException => Error(StatusCodes.Status404NotFound, exception.Message),
            UnauthorizedAccessException => new ForbidResult(),
            _ when ServiceWarmupMessage.IsDatabaseWarmupFailure(exception) =>
                Warmup(context, ServiceWarmupMessage.Dependencies),
            _ when ServiceWarmupMessage.IsLlmWarmupFailure(exception) =>
                Warmup(context, ServiceWarmupMessage.LlmAssistant),
            _ => Unexpected(context),
        };
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(int statusCode, string message)
        => new(new { error = message }) { StatusCode = statusCode };

    private static ObjectResult Warmup(ExceptionContext context, string message)
    {
        GetLogger(context).LogWarning(
            context.Exception,
            "Dependency warm-up interrupted {Action}",
            context.ActionDescriptor.DisplayName);
        return Error(StatusCodes.Status503ServiceUnavailable, message);
    }

    private static ObjectResult Unexpected(ExceptionContext context)
    {
        GetLogger(context).LogError(
            context.Exception,
            "AI-backed action {Action} failed",
            context.ActionDescriptor.DisplayName);
        return Error(StatusCodes.Status500InternalServerError, UnexpectedErrorMessage);
    }

    private static ILogger GetLogger(ExceptionContext context)
        => context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<AiServiceExceptionFilterAttribute>();
}
