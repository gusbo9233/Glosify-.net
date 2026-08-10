using System.Text.Json;
using Glosify.Filters;
using Glosify.Services.Ai;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class RequirePaidServicesAttributeTests
{
    [Fact]
    public async Task ClosedGateReturnsStable503WithoutInvokingTheAction()
    {
        var reset = new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
        using var services = new ServiceCollection()
            .AddSingleton<IPaidServiceGate>(new ClosedGate(reset))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
        var invoked = false;

        await new RequirePaidServicesAttribute().OnActionExecutionAsync(
            executing,
            () =>
            {
                invoked = true;
                return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
            });

        Assert.False(invoked);
        var result = Assert.IsType<ObjectResult>(executing.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("paid_services_budget_exhausted", json.GetProperty("code").GetString());
        Assert.Equal(reset, json.GetProperty("resetsAtUtc").GetDateTimeOffset());
    }

    private sealed class ClosedGate(DateTimeOffset reset) : IPaidServiceGate
    {
        public Task<PaidServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaidServiceStatus(false, PaidServiceGate.BudgetExhaustedReason, reset));

        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
