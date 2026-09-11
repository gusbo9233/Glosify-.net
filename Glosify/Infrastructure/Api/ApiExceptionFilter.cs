using Glosify.Services;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Glosify.Infrastructure.Api;

public sealed class ApiExceptionFilter(
    ILogger<ApiExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var request = context.HttpContext.Request;
        var expectsJson = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IApiBehaviorMetadata>() is not null
            || request.HasJsonContentType() || request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
        if (context.Exception is Glosify.Services.Abuse.SignupLimitException signup)
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (long)(signup.RetryAt - DateTimeOffset.UtcNow).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!expectsJson
            && context.Exception is Glosify.Services.Abuse.ResourceQuotaException or Glosify.Services.Abuse.SignupLimitException)
        {
            var text = new Glosify.Localization.UiTextStringLocalizer();
            var quota = context.Exception as Glosify.Services.Abuse.ResourceQuotaException;
            var message = quota is null ? context.Exception.Message : text[quota.MessageKey].Value;
            context.Result = new Microsoft.AspNetCore.Mvc.ViewResult { ViewName = "ResourceLimit",
                StatusCode = quota?.StatusCode ?? 429,
                ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<string>(
                    new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), context.ModelState) { Model = message } };
            context.ExceptionHandled = true;
            return;
        }
        if (!expectsJson
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
        else if (error.Value.StatusCode >= StatusCodes.Status500InternalServerError
            && context.Exception is not Glosify.Services.Abuse.ResourceQuotaException)
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
