using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glosify.Filters;
using Glosify.Infrastructure.Api;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Glosify.Tests;

public sealed class ApiProblemDetailsContractTests
{
    [Fact]
    public async Task AutomaticValidation_UsesStableProblemDetailsContract()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().PostAsJsonAsync("/_contract/validation", new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("error", out _));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task DomainException_UsesSpecificStableCode()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync("/_contract/conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("collection_name_conflict", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "A collection with this name already exists here.",
            json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConcurrentAssistantTurnUsesStableConflictProblemDetails()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync("/_contract/assistant-conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.Conflict, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(409, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task LegacyControllerError_IsNormalized()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync("/_contract/not-found");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("Missing probe.", json.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnexpectedException_IsSanitized()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync("/_contract/unexpected");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unexpected_error", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("private failure", json.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ClosedPaidServiceGateUsesTheStableHttpProblemDetailsContract()
    {
        var resetsAtUtc = new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
        using var factory = CreateFactory(new ClosedPaidServiceGate(resetsAtUtc));
        var response = await factory.CreateClient().GetAsync("/_contract/paid");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.PaidServicesBudgetExhausted,
            json.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "Paid features are unavailable because Glosify's monthly application budget has been reached.",
            json.RootElement.GetProperty("error").GetString());
        Assert.Equal(503, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(resetsAtUtc, json.RootElement.GetProperty("resetsAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task OpenApiDocument_IsAvailableInTesting()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("paths").TryGetProperty("/api/quizzes", out _));
    }

    private static WebApplicationFactory<Program> CreateFactory(IPaidServiceGate? paidServiceGate = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ContractProbeController).Assembly);
                if (paidServiceGate is not null)
                {
                    services.RemoveAll<IPaidServiceGate>();
                    services.AddSingleton(paidServiceGate);
                }
            });
        });

    private sealed class ClosedPaidServiceGate(DateTimeOffset resetsAtUtc) : IPaidServiceGate
    {
        public Task<PaidServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaidServiceStatus(
                false,
                "Paid features are unavailable because Glosify's monthly application budget has been reached.",
                resetsAtUtc));

        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

}

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("_contract")]
public sealed class ContractProbeController : ControllerBase
{
    [HttpPost("validation")]
    public IActionResult Validation(ContractProbeRequest request) => Ok(request);

    [HttpGet("conflict")]
    public IActionResult ConflictProbe() => throw new CollectionNameConflictException();

    [HttpGet("assistant-conflict")]
    public IActionResult AssistantConflictProbe() => throw new AssistantTurnInProgressException();

    [HttpGet("not-found")]
    public IActionResult NotFoundProbe() => NotFound("Missing probe.");

    [HttpGet("unexpected")]
    public IActionResult Unexpected() => throw new InvalidOperationException("private failure detail");

    [HttpGet("paid")]
    [RequirePaidServices]
    public IActionResult Paid() => Ok();
}

public sealed class ContractProbeRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string? Name { get; set; }
}
