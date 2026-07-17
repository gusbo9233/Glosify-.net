using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Glosify.Services.Ai;
using Glosify.Services.Speaking;
using Glosify.Services.Speech;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class SpeakingIntegrationTests
{
    [Fact]
    public async Task Speaking_page_renders_navigation_scenes_and_no_floating_assistant()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var document = await GetDocumentAsync(client);

        Assert.NotNull(document.QuerySelector("#speaking-app"));
        Assert.Equal(3, document.QuerySelectorAll("[data-avatar-scene]").Length);
        Assert.Equal(3, document.QuerySelectorAll("[data-avatar-choice]").Length);
        Assert.Null(document.QuerySelector("[data-assistant-panel]"));
        Assert.Contains(
            document.QuerySelectorAll("aside a"),
            link => string.Equals(link.GetAttribute("href"), "/Speaking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            document.QuerySelectorAll(".app-mobile-nav a"),
            link => link.TextContent.Contains("Speak", StringComparison.Ordinal));

        var svgIds = document.QuerySelectorAll("[data-avatar-scene] [id]")
            .Select(element => element.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        Assert.NotEmpty(svgIds);
        Assert.Equal(svgIds.Length, svgIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(
            document.Scripts,
            script => (script.GetAttribute("src") ?? string.Empty)
                .Contains("/lib/speech-sdk/distrib/browser/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Speaking_api_requires_antiforgery_for_authenticated_posts()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsync(
            "/api/speaking/sessions",
            JsonContent("""{"avatarId":"bartender","cefrLevel":"A2"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Speech_token_response_is_no_store_and_contains_no_key()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = Post("/api/speaking/speech-token", token);

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);
        Assert.Equal(
            "aad#/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/speech#browser-token",
            body.RootElement.GetProperty("authorizationToken").GetString());
        Assert.Equal("swedencentral", body.RootElement.GetProperty("region").GetString());
        Assert.False(body.RootElement.TryGetProperty("key", out _));
        Assert.DoesNotContain("speech-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_endpoint_validates_avatar_and_cefr()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var token = await GetAntiforgeryTokenAsync(client);

        using var badAvatar = Post(
            "/api/speaking/sessions",
            token,
            """{"avatarId":"unknown","cefrLevel":"A2"}""");
        using var badCefr = Post(
            "/api/speaking/sessions",
            token,
            """{"avatarId":"bartender","cefrLevel":"C2"}""");

        var avatarResponse = await client.SendAsync(badAvatar);
        var cefrResponse = await client.SendAsync(badCefr);

        Assert.Equal(HttpStatusCode.BadRequest, avatarResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, cefrResponse.StatusCode);
        Assert.Contains("\"error\"", await avatarResponse.Content.ReadAsStringAsync());
        Assert.Contains("\"error\"", await cefrResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Speech_token_endpoint_is_limited_to_twelve_requests_per_minute_per_user()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var token = await GetAntiforgeryTokenAsync(client);

        for (var requestNumber = 1; requestNumber <= 12; requestNumber++)
        {
            using var allowed = Post("/api/speaking/speech-token", token);
            var allowedResponse = await client.SendAsync(allowed);
            Assert.True(
                allowedResponse.IsSuccessStatusCode,
                $"Request {requestNumber} returned {(int)allowedResponse.StatusCode}.");
        }

        using var limited = Post("/api/speaking/speech-token", token);
        var limitedResponse = await client.SendAsync(limited);

        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiCreditService>();
                services.AddSingleton<IAiCreditService, PageCredits>();
                services.RemoveAll<ISpeakingService>();
                services.AddSingleton<ISpeakingService, FakeSpeakingService>();
                services.RemoveAll<ISpeechAuthorizationTokenService>();
                services.AddSingleton<ISpeechAuthorizationTokenService, FakeSpeechTokens>();
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.TestScheme;
                        options.DefaultChallengeScheme = TestAuthHandler.TestScheme;
                        options.DefaultForbidScheme = TestAuthHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.TestScheme,
                        _ => { });
            });
        });

    private static async Task<AngleSharp.Dom.IDocument> GetDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Speaking");
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var document = await GetDocumentAsync(client);
        return document.QuerySelector("input[name='__RequestVerificationToken']")
            ?.GetAttribute("value")
            ?? throw new InvalidOperationException("The speaking page did not render an antiforgery token.");
    }

    private static HttpRequestMessage Post(
        string url,
        string antiforgeryToken,
        string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        if (body is not null)
        {
            request.Content = JsonContent(body);
        }

        return request;
    }

    private static StringContent JsonContent(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "SpeakingTests";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, "learner-1"),
                new(ClaimTypes.Email, "learner@example.test"),
                new(ClaimTypes.Name, "learner@example.test"),
            ];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }

    private sealed class FakeSpeechTokens : ISpeechAuthorizationTokenService
    {
        public bool IsConfigured => true;

        public Task<SpeechAuthorizationToken> GetTokenAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpeechAuthorizationToken(
                "aad#/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/speech#browser-token",
                "swedencentral",
                DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    private sealed class FakeSpeakingService : ISpeakingService
    {
        public Task<SpeakingSessionCreated> CreateSessionAsync(
            string userId,
            SpeakingAvatarDefinition avatar,
            CefrLevel cefrLevel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpeakingSessionCreated(
                Guid.NewGuid(),
                avatar.Slug,
                avatar.Name,
                avatar.Voice,
                new SpeakingOpeningTurn(avatar.OpeningPolish, avatar.OpeningEnglish)));

        public Task<SpeakingTurn> SendTurnAsync(
            Guid sessionId,
            string userId,
            string text,
            SpeakingInputMode inputMode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpeakingTurn
            {
                ReplyPolish = "Dobrze.",
                ReplyEnglish = "All right.",
                Coach = new SpeakingCoach(),
            });

        public Task DeleteSessionAsync(
            Guid sessionId,
            string userId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class PageCredits : IAiCreditService
    {
        public Task<AiCreditAccountView> GetOrCreateAccountAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiCreditAccountView(userId, 100, 0, 100, null));

        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(
            string userId,
            int count = 25,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiCreditTransaction>>([]);

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiCreditReservation(
                Guid.NewGuid(),
                context.UserId,
                1,
                estimatedTokens));

        public Task CommitUsageAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task GrantAsync(
            string adminUserId,
            string targetUserId,
            int credits,
            string note,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
