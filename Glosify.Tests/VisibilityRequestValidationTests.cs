using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Glosify.Infrastructure.Api;
using Glosify.Models.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Glosify.Tests;

public sealed class VisibilityRequestValidationTests
{
    private const string UserId = "visibility-user";

    [Theory]
    [InlineData("quizzes")]
    [InlineData("collections")]
    public async Task SetVisibility_rejects_an_empty_json_object(string resource)
    {
        using var factory = CreateAuthenticatedFactory();

        var response = await factory.CreateClient().PutAsJsonAsync(
            $"/api/{resource}/{Guid.NewGuid()}/visibility",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = Assert.IsType<HttpValidationProblemDetails>(
            await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>());
        Assert.Equal(ApiErrorCodes.ValidationFailed, problem.Extensions["code"]?.ToString());
        Assert.Contains(
            problem.Errors.SelectMany(error => error.Value),
            message => message.Contains(
                nameof(SetVisibilityRequest.IsPublic),
                StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplicationFactory<Program> CreateAuthenticatedFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPolicyEvaluator>();
                services.AddSingleton<IPolicyEvaluator, AuthenticatedPolicyEvaluator>();
            });
        });

    private sealed class AuthenticatedPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(
            AuthorizationPolicy policy,
            HttpContext context)
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, UserId)],
                authenticationType: "test");
            var principal = new ClaimsPrincipal(identity);
            context.User = principal;
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "test")));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource) => Task.FromResult(PolicyAuthorizationResult.Success());
    }
}
