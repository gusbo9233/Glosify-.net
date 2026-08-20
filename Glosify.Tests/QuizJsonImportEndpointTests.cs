using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Glosify.Controllers;
using Glosify.Infrastructure.Api;
using Glosify.Models.QuizImports;
using Glosify.Services.Ai;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizJsonImportEndpointTests
{
    private const string UserId = "json-import-user";
    private const string ValidJson = """
        {"version":1,"source_language":"English","quizzes":[{"name":"Basics","words":[{"word":"dom","translation":"house"}],"sentences":[]}],"collections":[]}
        """;

    [Fact]
    public async Task Preview_RequiresAuthentication()
    {
        var imports = new RecordingImportService();
        using var factory = CreateFactory(imports, authenticated: false);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsync(
            "/Quiz/PreviewJsonImport",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Json"] = ValidJson }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, imports.PreviewCount);
    }

    [Fact]
    public async Task Preview_RequiresAntiforgeryAndNeverInvokesAiRepair()
    {
        var imports = new RecordingImportService { RejectPreview = true };
        var repairs = new RecordingRepairService();
        using var factory = CreateFactory(imports, repairs: repairs);
        var client = factory.CreateClient();

        var missingToken = await client.PostAsync(
            "/Quiz/PreviewJsonImport",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Json"] = "invalid" }));

        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Equal(0, imports.PreviewCount);

        var invalidPreview = await SendWithAntiforgeryAsync(
            factory,
            client,
            "/Quiz/PreviewJsonImport",
            "invalid");

        Assert.Equal(HttpStatusCode.BadRequest, invalidPreview.StatusCode);
        Assert.Equal("application/problem+json", invalidPreview.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await invalidPreview.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.ValidationFailed, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "Expected a JSON object.",
            problem.RootElement.GetProperty("errors").GetProperty("$")[0].GetString());
        Assert.Equal("invalid", problem.RootElement.GetProperty("canonicalJson").GetString());
        Assert.Equal(1, imports.PreviewCount);
        Assert.Equal(0, repairs.RepairCount);
    }

    [Fact]
    public async Task Apply_ForwardsTheExactCurrentJsonInsteadOfAStoredPreview()
    {
        var imports = new RecordingImportService();
        using var factory = CreateFactory(imports);
        var client = factory.CreateClient();

        var previewResponse = await SendWithAntiforgeryAsync(
            factory,
            client,
            "/Quiz/PreviewJsonImport",
            ValidJson);
        previewResponse.EnsureSuccessStatusCode();

        const string changedJson = "{\"version\":1,\"source_language\":\"Swedish\",\"quizzes\":[],\"collections\":[]}";
        var applyResponse = await SendWithAntiforgeryAsync(
            factory,
            client,
            "/Quiz/ApplyJsonImport",
            changedJson);

        applyResponse.EnsureSuccessStatusCode();
        var result = Assert.IsType<QuizJsonImportApplyResponse>(
            await applyResponse.Content.ReadFromJsonAsync<QuizJsonImportApplyResponse>());
        Assert.Equal(2, result.CollectionCount);
        Assert.Equal(3, result.QuizCount);
        Assert.Equal(ValidJson, imports.LastPreviewJson);
        Assert.Equal(changedJson, imports.LastApplyJson);
        Assert.Equal("Polish", imports.LastTargetLanguage);
        Assert.Equal(UserId, imports.LastUserId);
    }

    [Fact]
    public async Task ExplicitAiRepair_UsesTheUnprocessableProblemDetailsContract()
    {
        var repairs = new RecordingRepairService
        {
            Failure = new QuizJsonImportAiUnprocessableException(),
        };
        using var factory = CreateFactory(new RecordingImportService(), repairs: repairs);
        var response = await SendWithAntiforgeryAsync(
            factory,
            factory.CreateClient(),
            "/Quiz/RepairJsonImportWithAi",
            "invalid");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.UnprocessableEntity, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, repairs.RepairCount);
    }

    [Fact]
    public async Task ExplicitAiRepair_ReturnsPostRepairValidationErrorsWithThe422Contract()
    {
        var repairs = new RecordingRepairService
        {
            Failure = new QuizJsonImportAiUnprocessableException(
                new Dictionary<string, string[]> { ["$.quizzes[0].words[0].translation"] = ["A translation is required."] },
                "{\"version\":1}"),
        };
        using var factory = CreateFactory(new RecordingImportService(), repairs: repairs);
        var response = await SendWithAntiforgeryAsync(
            factory,
            factory.CreateClient(),
            "/Quiz/RepairJsonImportWithAi",
            "invalid");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.UnprocessableEntity, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "A translation is required.",
            problem.RootElement.GetProperty("errors")
                .GetProperty("$.quizzes[0].words[0].translation")[0]
                .GetString());
        Assert.Equal("{\"version\":1}", problem.RootElement.GetProperty("canonicalJson").GetString());
    }

    [Fact]
    public async Task ExplicitAiRepair_RejectsFreestyleWithTheSharedProblemDetailsContract()
    {
        var repairs = new RecordingRepairService();
        using var factory = CreateFactory(
            new RecordingImportService(),
            repairs: repairs,
            language: "Freestyle");
        var response = await SendWithAntiforgeryAsync(
            factory,
            factory.CreateClient(),
            "/Quiz/RepairJsonImportWithAi",
            ValidJson);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.FeatureUnavailableForMode, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "AI language repair is not available in Freestyle mode.",
            problem.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, repairs.RepairCount);
    }

    [Fact]
    public void ImportActions_DeclareAntiforgeryAndTheBrowserRequestSizeLimit()
    {
        foreach (var actionName in new[]
                 {
                     nameof(QuizImportController.PreviewJsonImport),
                     nameof(QuizImportController.RepairJsonImportWithAi),
                     nameof(QuizImportController.ApplyJsonImport),
                 })
        {
            var method = typeof(QuizImportController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
            Assert.Single(method!.GetCustomAttributes<ValidateAntiForgeryTokenAttribute>());
            var limit = Assert.Single(method!.GetCustomAttributes<RequestSizeLimitAttribute>());
            Assert.Equal(96 * 1024, ((IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        RecordingImportService imports,
        bool authenticated = true,
        RecordingRepairService? repairs = null,
        string language = "Polish") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuizJsonImportService>();
                services.AddSingleton<IQuizJsonImportService>(imports);
                services.RemoveAll<IQuizJsonImportRepairService>();
                services.AddSingleton<IQuizJsonImportRepairService>(repairs ?? new RecordingRepairService());
                services.RemoveAll<ILanguageContext>();
                services.AddSingleton<ILanguageContext>(new FixedLanguageContext(language));
                if (authenticated)
                {
                    services.RemoveAll<IPolicyEvaluator>();
                    services.AddSingleton<IPolicyEvaluator, AuthenticatedPolicyEvaluator>();
                }
            });
        });

    private static async Task<HttpResponseMessage> SendWithAntiforgeryAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string path,
        string json)
    {
        var antiforgery = factory.Services.GetRequiredService<IAntiforgery>();
        var tokenContext = new DefaultHttpContext
        {
            RequestServices = factory.Services,
            User = CreatePrincipal(),
        };
        var tokens = antiforgery.GetAndStoreTokens(tokenContext);
        var cookie = tokenContext.Response.Headers.SetCookie
            .Select(value => value?.Split(';', 2)[0])
            .Single(value => !string.IsNullOrWhiteSpace(value));
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Json"] = json }),
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add(tokens.HeaderName!, tokens.RequestToken!);
        return await client.SendAsync(request);
    }

    private static ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, UserId)],
        authenticationType: "test"));

    private sealed class AuthenticatedPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
        {
            var principal = CreatePrincipal();
            context.User = principal;
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource) => Task.FromResult(PolicyAuthorizationResult.Success());
    }

    private sealed class FixedLanguageContext(string? language) : ILanguageContext
    {
        public string? CurrentLanguage { get; } = language;
        public IReadOnlyList<string> SupportedLanguages => CurrentLanguage is null ? [] : [CurrentLanguage];
        public bool TrySetLanguage(string value) => false;
        public void Clear() { }
    }

    private sealed class RecordingImportService : IQuizJsonImportService
    {
        public int PreviewCount { get; private set; }
        public string? LastPreviewJson { get; private set; }
        public string? LastApplyJson { get; private set; }
        public string? LastTargetLanguage { get; private set; }
        public string? LastUserId { get; private set; }
        public bool RejectPreview { get; init; }

        public Task<QuizJsonImportPreview> PreviewAsync(
            string json,
            string targetLanguage,
            Guid? parentCollectionId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            PreviewCount++;
            LastPreviewJson = json;
            LastTargetLanguage = targetLanguage;
            LastUserId = userId;
            if (RejectPreview)
            {
                throw new QuizJsonImportValidationException(
                    new Dictionary<string, string[]> { ["$"] = ["Expected a JSON object."] },
                    json);
            }

            return Task.FromResult(new QuizJsonImportPreview(
                json,
                false,
                targetLanguage,
                parentCollectionId,
                new QuizJsonImportTotals(0, 1, 1, 0),
                [new QuizJsonImportQuizPreview("Basics", "English", targetLanguage, 1, 0)],
                [],
                []));
        }

        public Task<QuizJsonImportResult> ApplyAsync(
            string json,
            string targetLanguage,
            Guid? parentCollectionId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            LastApplyJson = json;
            LastTargetLanguage = targetLanguage;
            LastUserId = userId;
            return Task.FromResult(new QuizJsonImportResult(2, 3, 4, 5));
        }
    }

    private sealed class RecordingRepairService : IQuizJsonImportRepairService
    {
        public int RepairCount { get; private set; }
        public Exception? Failure { get; init; }

        public Task<QuizJsonImportPreview> RepairAsync(
            string json,
            string targetLanguage,
            Guid? parentCollectionId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            RepairCount++;
            throw Failure ?? new NotSupportedException();
        }
    }
}
