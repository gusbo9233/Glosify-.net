using Glosify.Services;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Glosify.Infrastructure.Api;

public sealed class ApiExceptionFilter(
    ILogger<ApiExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IApiBehaviorMetadata>() is null
            || context.Exception is OperationCanceledException
                && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var error = ApiExceptionMapper.Map(context.Exception);
        if (error is null && ServiceWarmupMessage.IsDatabaseWarmupFailure(context.Exception))
        {
            error = new ApiError(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.DependencyUnavailable,
                ServiceWarmupMessage.Dependencies);
        }
        else if (error is null && ServiceWarmupMessage.IsLlmWarmupFailure(context.Exception))
        {
            error = new ApiError(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.DependencyUnavailable,
                ServiceWarmupMessage.LlmAssistant);
        }

        if (error is null)
        {
            logger.LogError(context.Exception, "API action {Action} failed", context.ActionDescriptor.DisplayName);
            error = new ApiError(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.Unexpected,
                "An unexpected error occurred. Please try again.");
        }
        else if (error.Value.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogWarning(context.Exception, "API dependency failure in {Action}", context.ActionDescriptor.DisplayName);
        }

        context.Result = GlosifyProblemDetails.Result(
            context.HttpContext,
            error.Value.StatusCode,
            error.Value.Code,
            error.Value.Detail);
        context.ExceptionHandled = true;
    }
}
