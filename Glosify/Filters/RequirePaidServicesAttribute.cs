using Glosify.Infrastructure.Api;
using Glosify.Services.Ai;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Glosify.Filters;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequirePaidServicesAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<IPaidServiceGate>();
        var status = await gate.GetStatusAsync(context.HttpContext.RequestAborted);
        if (status.Available)
        {
            await next();
            return;
        }

        context.Result = GlosifyProblemDetails.Result(
            context.HttpContext,
            StatusCodes.Status503ServiceUnavailable,
            ApiErrorCodes.PaidServicesBudgetExhausted,
            status.Reason ?? PaidServiceGate.BudgetExhaustedReason,
            new Dictionary<string, object?> { ["resetsAtUtc"] = status.ResetsAtUtc });
    }
}
