using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Glosify.Infrastructure.Api;

public sealed class ApiProblemDetailsResultFilter : IAsyncAlwaysRunResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IApiBehaviorMetadata>() is not null
            && TryReadError(context.Result, out var statusCode, out var detail, out var extensions))
        {
            context.Result = GlosifyProblemDetails.Result(
                context.HttpContext,
                statusCode,
                GlosifyProblemDetails.CodeForStatus(statusCode),
                detail,
                extensions);
        }

        await next();
    }

    private static bool TryReadError(
        IActionResult result,
        out int statusCode,
        out string? detail,
        out IReadOnlyDictionary<string, object?>? extensions)
    {
        detail = null;
        extensions = null;

        switch (result)
        {
            case ObjectResult { Value: ProblemDetails problem } objectResult:
                statusCode = problem.Status ?? objectResult.StatusCode ?? StatusCodes.Status500InternalServerError;
                if (statusCode < 400 || problem.Extensions.ContainsKey("code"))
                {
                    return false;
                }

                detail = problem.Detail;
                extensions = problem.Extensions
                    .Where(extension => extension.Key is not "error" and not "traceId")
                    .ToDictionary(extension => extension.Key, extension => extension.Value);
                return true;
            case ObjectResult objectResult when (objectResult.StatusCode ?? 200) >= 400:
                statusCode = objectResult.StatusCode!.Value;
                ReadLegacyBody(objectResult.Value, out detail, out extensions);
                return true;
            case StatusCodeResult statusResult when statusResult.StatusCode >= 400:
                statusCode = statusResult.StatusCode;
                return true;
            default:
                statusCode = 0;
                return false;
        }
    }

    private static void ReadLegacyBody(
        object? value,
        out string? detail,
        out IReadOnlyDictionary<string, object?>? extensions)
    {
        detail = value as string;
        extensions = null;
        if (value is null || value is string)
        {
            return;
        }

        var json = JsonSerializer.SerializeToElement(value);
        if (json.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            detail = error.GetString();
        }

        if (json.TryGetProperty("errors", out var errors))
        {
            extensions = new Dictionary<string, object?>
            {
                ["errors"] = errors.Clone(),
            };
        }
    }
}
