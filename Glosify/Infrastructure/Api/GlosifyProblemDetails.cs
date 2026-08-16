using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Glosify.Infrastructure.Api;

public static class GlosifyProblemDetails
{
    public static ObjectResult Result(
        HttpContext httpContext,
        int statusCode,
        string code,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = Create(httpContext, statusCode, code, detail);
        if (extensions is not null)
        {
            foreach (var extension in extensions)
            {
                problem.Extensions[extension.Key] = extension.Value;
            }
        }

        return ToResult(problem, statusCode);
    }

    public static ObjectResult ValidationResult(ActionContext actionContext)
    {
        var errors = actionContext.ModelState
            .Where(entry => entry.Value?.ValidationState == ModelValidationState.Invalid)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value!.Errors
                    .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? "The supplied value is invalid."
                        : error.ErrorMessage)
                    .ToArray(),
                StringComparer.Ordinal);

        var problem = new HttpValidationProblemDetails(errors)
        {
            Type = $"urn:glosify:error:{ApiErrorCodes.ValidationFailed}",
            Title = TitleFor(StatusCodes.Status400BadRequest),
            Status = StatusCodes.Status400BadRequest,
            Detail = "One or more validation errors occurred.",
            Instance = actionContext.HttpContext.Request.Path,
        };
        AddCommonExtensions(actionContext.HttpContext, problem, ApiErrorCodes.ValidationFailed);
        return ToResult(problem, StatusCodes.Status400BadRequest);
    }

    public static ObjectResult ValidationResult(
        HttpContext httpContext,
        IReadOnlyDictionary<string, string[]> errors,
        string? canonicalJson = null,
        int statusCode = StatusCodes.Status400BadRequest,
        string code = ApiErrorCodes.ValidationFailed)
    {
        var problem = new HttpValidationProblemDetails(errors)
        {
            Type = $"urn:glosify:error:{code}",
            Title = TitleFor(statusCode),
            Status = statusCode,
            Detail = "One or more validation errors occurred.",
            Instance = httpContext.Request.Path,
        };
        if (!string.IsNullOrWhiteSpace(canonicalJson))
        {
            problem.Extensions["canonicalJson"] = canonicalJson;
        }
        AddCommonExtensions(httpContext, problem, code);
        return ToResult(problem, statusCode);
    }

    public static ProblemDetails Create(
        HttpContext httpContext,
        int statusCode,
        string code,
        string? detail = null)
    {
        var title = TitleFor(statusCode);
        var problem = new ProblemDetails
        {
            Type = $"urn:glosify:error:{code}",
            Title = title,
            Status = statusCode,
            Detail = string.IsNullOrWhiteSpace(detail) ? title : detail,
            Instance = httpContext.Request.Path,
        };
        AddCommonExtensions(httpContext, problem, code);
        return problem;
    }

    public static string CodeForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => ApiErrorCodes.BadRequest,
        StatusCodes.Status401Unauthorized => ApiErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ApiErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ApiErrorCodes.NotFound,
        StatusCodes.Status402PaymentRequired => ApiErrorCodes.PaymentRequired,
        StatusCodes.Status409Conflict => ApiErrorCodes.Conflict,
        StatusCodes.Status410Gone => ApiErrorCodes.Gone,
        StatusCodes.Status422UnprocessableEntity => ApiErrorCodes.UnprocessableEntity,
        StatusCodes.Status429TooManyRequests => ApiErrorCodes.RateLimited,
        StatusCodes.Status503ServiceUnavailable => ApiErrorCodes.DependencyUnavailable,
        StatusCodes.Status504GatewayTimeout => ApiErrorCodes.Timeout,
        _ when statusCode >= 500 => ApiErrorCodes.Unexpected,
        _ => "request_failed",
    };

    private static ObjectResult ToResult(ProblemDetails problem, int statusCode)
    {
        var result = new ObjectResult(problem) { StatusCode = statusCode };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static void AddCommonExtensions(HttpContext httpContext, ProblemDetails problem, string code)
    {
        problem.Extensions["code"] = code;
        problem.Extensions["error"] = problem.Detail ?? problem.Title ?? "Request failed.";
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
    }

    private static string TitleFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status402PaymentRequired => "Payment Required",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status410Gone => "Gone",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable Entity",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        StatusCodes.Status501NotImplemented => "Not Implemented",
        StatusCodes.Status502BadGateway => "Bad Gateway",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        StatusCodes.Status504GatewayTimeout => "Gateway Timeout",
        _ => "Request Failed",
    };
}
