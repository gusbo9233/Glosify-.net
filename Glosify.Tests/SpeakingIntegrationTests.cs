using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Glosify.Services.Ai;
using Glosify.Services.Language;
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
    public async Task Speaking_page_redirects_to_language_selection_when_none_is_selected()
    {
        using var factory = CreateFactory(null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/Speaking");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Languages", response.Headers.Location?.OriginalString);
    }

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
        var microphone = document.QuerySelector("#speaking-mic");
        Assert.Equal("false", microphone?.GetAttribute("aria-pressed"));
        Assert.Contains("Speak to type", microphone?.TextContent ?? string.Empty);
        var avatarRecorder = document.QuerySelector("#speaking-avatar-record");
        Assert.Equal("false", avatarRecorder?.GetAttribute("aria-pressed"));
        Assert.Contains("Hold to speak & send", avatarRecorder?.TextContent ?? string.Empty);
        var avatarTemplate = Assert.IsAssignableFrom<IHtmlTemplateElement>(
            document.QuerySelector("#speaking-avatar-message-template"));
        var messageFlip = avatarTemplate.Content.QuerySelector("[data-message-flip]");
        Assert.Equal("button", messageFlip?.LocalName);
        Assert.Equal("false", messageFlip?.GetAttribute("aria-pressed"));
        Assert.Equal(
            "false",
            messageFlip?.QuerySelector(".speaking-message-front")?.GetAttribute("aria-hidden"));
        Assert.Equal(
            "true",
            messageFlip?.QuerySelector(".speaking-message-back")?.GetAttribute("aria-hidden"));
        Assert.Null(document.QuerySelector("#speaking-interactive-toggle"));
        Assert.Null(document.QuerySelector("#speaking-interactive-control"));
        Assert.NotNull(document.QuerySelector("#speaking-bar-menu"));
        Assert.Equal(6, document.QuerySelectorAll("[data-menu-drink]").Length);
        Assert.NotNull(document.QuerySelector("#speaking-wallet"));
        Assert.Null(document.QuerySelector(
            ".speaking-scene-bartender.has-active-drink"));
        Assert.NotNull(document.QuerySelector(
            "[data-bartender-active-drink][transform] [data-bartender-drink-motion]"));
        Assert.Equal(2, document.QuerySelectorAll(
            "[data-bartender-tap][transform] [data-bartender-pour-motion]").Length);
        Assert.Equal(2, document.QuerySelectorAll(
            "[data-bartender-pour-effect] .bartender-pour-stream").Length);
        Assert.Equal(2, document.QuerySelectorAll(
            "[data-bartender-pour-effect] .bartender-service-glass").Length);
        Assert.NotNull(document.QuerySelector(
            "[data-bartender-snack][transform] [data-bartender-snack-motion]"));
        Assert.NotNull(document.QuerySelector(
            ".avatar-gesture > [data-bartender-polish-gesture]"));
    }

    [Fact]
    public async Task Speaking_page_hides_interactive_controls_behind_the_operational_flag()
    {
        using var factory = CreateFactory(interactiveEnabled: false);
        var client = factory.CreateClient();

        var document = await GetDocumentAsync(client);

        Assert.Null(document.QuerySelector("#speaking-interactive-toggle"));
        Assert.Null(document.QuerySelector("#speaking-wallet"));
        Assert.Null(document.QuerySelector("#speaking-bar-menu"));
    }

    [Theory]
    [InlineData("Estonian", "maarja")]
    [InlineData("German", "hanna")]
    [InlineData("Polish", "bartender")]
    [InlineData("Ukrainian", "oksana")]
    public async Task Speaking_page_only_exposes_avatars_for_the_selected_language(
        string language,
        string expectedDefaultAvatar)
    {
        using var factory = CreateFactory(language);
        var client = factory.CreateClient();

        var document = await GetDocumentAsync(client);
        var root = Assert.IsAssignableFrom<IHtmlElement>(document.QuerySelector("#speaking-app"));
        using var pageData = JsonDocument.Parse(
            root.GetAttribute("data-speaking-page")
            ?? throw new InvalidOperationException("Speaking page data is missing."));

        Assert.Equal(language, pageData.RootElement.GetProperty("language").GetString());
        Assert.Equal(expectedDefaultAvatar, pageData.RootElement.GetProperty("defaultAvatarId").GetString());
        Assert.Equal(3, pageData.RootElement.GetProperty("avatars").GetArrayLength());
        Assert.All(
            pageData.RootElement.GetProperty("avatars").EnumerateArray(),
            avatar => Assert.Equal(language, avatar.GetProperty("language").GetString()));
        Assert.Equal(3, document.QuerySelectorAll("[data-avatar-choice]").Length);
        Assert.Equal(3, document.QuerySelectorAll("[data-avatar-scene]").Length);
        var svgIds = document.QuerySelectorAll("[data-avatar-scene] [id]")
            .Select(element => element.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        Assert.Equal(svgIds.Length, svgIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Speaking_client_supports_hold_to_send_and_silence_ended_draft_transcription()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/speaking.js");

        Assert.Contains("startContinuousRecognitionAsync", script, StringComparison.Ordinal);
        Assert.Contains("stopContinuousRecognitionAsync", script, StringComparison.Ordinal);
        Assert.Contains("elements.avatarRecord.addEventListener(\"pointerdown\"", script, StringComparison.Ordinal);
        Assert.Contains("elements.avatarRecord.addEventListener(\"pointerup\"", script, StringComparison.Ordinal);
        Assert.Contains("elements.mic.addEventListener(\"click\"", script, StringComparison.Ordinal);
        Assert.Contains("function scheduleSilenceStop", script, StringComparison.Ordinal);
        Assert.Contains("recognition.autoStop", script, StringComparison.Ordinal);
        Assert.Contains("if (recognition.autoSend)", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener(\"pointerdown\"", script, StringComparison.Ordinal);
        Assert.Contains("await sendCurrentMessage();", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Speaking_client_flips_individual_avatar_messages_to_their_translation()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/speaking.js");

        Assert.Contains("function setMessageTranslation", script, StringComparison.Ordinal);
        Assert.Contains("root.querySelectorAll(\"[data-message-flip]\")", script, StringComparison.Ordinal);
        Assert.Contains("messageFlip.addEventListener(\"click\"", script, StringComparison.Ordinal);
        Assert.Contains("\"is-flipped\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Speaking_client_uses_the_selected_avatar_locale_for_speech()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/speaking.js");

        Assert.Contains("avatar?.locale || pageData.locale", script, StringComparison.Ordinal);
        Assert.Contains("escapeXml(avatar.locale)", script, StringComparison.Ordinal);
        Assert.Contains("avatar?.languageCode || pageData.languageCode", script, StringComparison.Ordinal);
        Assert.DoesNotContain("speechRecognitionLanguage = \"pl-PL\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_client_uses_authoritative_snapshots_and_reduced_motion_queue()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/speaking.js");
        var styles = await client.GetStringAsync("/css/speaking.css");

        Assert.DoesNotContain("interactiveMode: state.interactiveMode", script, StringComparison.Ordinal);
        Assert.Contains("/actions`", script, StringComparison.Ordinal);
        Assert.Contains("function playSceneActions", script, StringComparison.Ordinal);
        Assert.Contains("function enqueueSceneActions", script, StringComparison.Ordinal);
        Assert.Contains("function cancelSceneActions", script, StringComparison.Ordinal);
        Assert.Contains("function waitForSceneAnimation", script, StringComparison.Ordinal);
        Assert.Contains("function applyInteractionSnapshot", script, StringComparison.Ordinal);
        Assert.Contains("function applySceneSnapshot", script, StringComparison.Ordinal);
        Assert.Contains("state.sceneQueue = state.sceneQueue", script, StringComparison.Ordinal);
        Assert.Contains("state.speechGeneration += 1", script, StringComparison.Ordinal);
        Assert.Contains("[elements.menuToggle, elements.walletToggle]", script, StringComparison.Ordinal);
        Assert.Contains("[elements.drinkAction, elements.snackAction]", script, StringComparison.Ordinal);
        Assert.Contains("That moment passed. Keep chatting", script, StringComparison.Ordinal);
        Assert.Contains("function closeSynthesizer", script, StringComparison.Ordinal);
        Assert.Contains("function refocusComposerForKeyboardUsers", script, StringComparison.Ordinal);
        Assert.Contains(
            "(hover: hover) and (pointer: fine)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "elements.textarea.focus({ preventScroll: true })",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "setBusy(false);\n            elements.textarea.focus();",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("await presentTurn(turn)", script, StringComparison.Ordinal);
        Assert.Contains(
            "{ updateScene: false, updateActions: false }",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "enqueueSceneActions(turn.sceneActions, turn.interaction)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("sceneActionsPending: false", script, StringComparison.Ordinal);
        Assert.Contains(
            "control.disabled =\n                    interactionLocked\n                    || state.sceneActionsPending",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.sceneActionsPending = true;\n        elements.interactiveLayer?.setAttribute(\"aria-busy\", \"true\");\n        hideInteractionActions();",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "applyInteractionActions(snapshot);\n                state.sceneActionsPending = false;\n                elements.interactiveLayer?.setAttribute(\"aria-busy\", \"false\");",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (state.busy\n            || state.sceneActionsPending",
            script,
            StringComparison.Ordinal);
        var scenePlayback = script.IndexOf(
            "async function playSceneActions",
            StringComparison.Ordinal);
        var commandPlayback = script.IndexOf(
            "await playSceneCommand(command, generation)",
            scenePlayback,
            StringComparison.Ordinal);
        Assert.True(commandPlayback > scenePlayback);
        Assert.True(commandPlayback < script.IndexOf(
            "applySceneSnapshot(snapshot)",
            commandPlayback,
            StringComparison.Ordinal));
        var pourCommand = script.IndexOf(
            "case \"pourAndServe\"",
            StringComparison.Ordinal);
        var pourClass = script.IndexOf(
            "scene?.classList.add(\"is-pouring\")",
            pourCommand,
            StringComparison.Ordinal);
        var pourAnimation = script.IndexOf(
            "\"speaking-bartender-pour\"",
            pourClass,
            StringComparison.Ordinal);
        var serveClass = script.IndexOf(
            "scene?.classList.add(\"is-serving\")",
            pourAnimation,
            StringComparison.Ordinal);
        var serveAnimation = script.IndexOf(
            "\"speaking-bartender-serve\"",
            serveClass,
            StringComparison.Ordinal);
        var activeDrinkClass = script.IndexOf(
            "scene?.classList.add(\"has-active-drink\")",
            serveAnimation,
            StringComparison.Ordinal);
        var finishServing = script.IndexOf(
            "scene?.classList.remove(\"is-serving\")",
            activeDrinkClass,
            StringComparison.Ordinal);
        Assert.True(pourCommand >= 0);
        Assert.True(pourClass > pourCommand);
        Assert.True(pourAnimation > pourClass);
        Assert.True(serveClass > pourAnimation);
        Assert.True(serveAnimation > serveClass);
        Assert.True(activeDrinkClass > serveAnimation);
        Assert.True(finishServing > activeDrinkClass);
        Assert.DoesNotContain(
            "classList.add(\"has-active-drink\", \"is-pouring\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains("\"animationend\"", script, StringComparison.Ordinal);
        Assert.Contains("\"animationcancel\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "event.animationName === expectedAnimationName",
            script,
            StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", script, StringComparison.Ordinal);
        Assert.DoesNotContain("glosify-speaking-bartender-interactive", script, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 620px)", styles, StringComparison.Ordinal);
        Assert.Contains(
            "@media (max-width: 900px), (pointer: coarse)",
            styles,
            StringComparison.Ordinal);
        Assert.Contains("height: clamp(700px, 78vh, 800px);", styles, StringComparison.Ordinal);
        Assert.Contains(".speaking-messages {\n    min-height: 0;", styles, StringComparison.Ordinal);
        Assert.Contains(
            ".speaking-scene-bartender.is-serving [data-bartender-drink-motion]",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            "@keyframes speaking-bartender-beer-stream",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            "@keyframes speaking-bartender-glass-fill",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            ".speaking-scene-bartender.is-serving [data-bartender-active-drink]",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            ".speaking-scene-bartender.has-active-drink [data-bartender-active-drink]",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            """
            .speaking-scene-bartender [data-bartender-active-drink],
            .speaking-scene-bartender [data-bartender-coaster] {
                opacity: 0;
            """,
            styles,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".speaking-scene-bartender.is-interactive:not(.has-active-drink)",
            styles,
            StringComparison.Ordinal);
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
    public async Task Session_endpoint_rejects_an_avatar_from_another_language()
    {
        using var factory = CreateFactory("German");
        var client = factory.CreateClient();
        var token = await GetAntiforgeryTokenAsync(client);
        using var polishAvatar = Post(
            "/api/speaking/sessions",
            token,
            """{"avatarId":"bartender","cefrLevel":"A2"}""");
        using var germanAvatar = Post(
            "/api/speaking/sessions",
            token,
            """{"avatarId":"hanna","cefrLevel":"A2"}""");

        var rejected = await client.SendAsync(polishAvatar);
        var accepted = await client.SendAsync(germanAvatar);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        accepted.EnsureSuccessStatusCode();
        Assert.Contains("German", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bartender_session_automatically_returns_an_authoritative_wallet_snapshot()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = Post(
            "/api/speaking/sessions",
            token,
            """{"avatarId":"bartender","cefrLevel":"A2"}""");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var interaction = body.RootElement.GetProperty("interaction");
        Assert.Equal(100, interaction.GetProperty("walletBalance").GetInt32());
        Assert.Equal(6, interaction.GetProperty("menu").GetArrayLength());
        Assert.Equal(6, interaction.GetProperty("wallet").GetArrayLength());
    }

    [Fact]
    public async Task Non_bartender_session_stays_noninteractive_without_a_client_mode_flag()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = Post(
            "/api/speaking/sessions",
            token,
            """{"avatarId":"kasia","cefrLevel":"A2"}""");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("interaction").ValueKind);
    }

    [Fact]
    public async Task Interactive_action_endpoint_requires_antiforgery_and_valid_action()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        var missingToken = await client.PostAsync(
            $"/api/speaking/sessions/{sessionId}/actions",
            JsonContent("""{"action":"drink"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var token = await GetAntiforgeryTokenAsync(client);
        using var malformed = Post(
            $"/api/speaking/sessions/{sessionId}/actions",
            token,
            """{"action":"invented"}""");
        var malformedResponse = await client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);

        using var badDenomination = Post(
            $"/api/speaking/sessions/{sessionId}/actions",
            token,
            """{"action":"submitPayment","denominations":{"3":1}}""");
        var badDenominationResponse = await client.SendAsync(badDenomination);
        Assert.Equal(HttpStatusCode.BadRequest, badDenominationResponse.StatusCode);

        using var valid = Post(
            $"/api/speaking/sessions/{sessionId}/actions",
            token,
            """{"action":"submitPayment","denominations":{"20":1}}""");
        var validResponse = await client.SendAsync(valid);
        validResponse.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await validResponse.Content.ReadAsStringAsync());
        Assert.Equal(100, body.RootElement.GetProperty("interaction")
            .GetProperty("walletBalance").GetInt32());
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

    private static WebApplicationFactory<Program> CreateFactory(
        string? language = "Polish",
        bool interactiveEnabled = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiCreditService>();
                services.AddSingleton<IAiCreditService, PageCredits>();
                services.RemoveAll<ISpeakingService>();
                services.AddSingleton<ISpeakingService, FakeSpeakingService>();
                services.PostConfigure<SpeakingOptions>(
                    options => options.InteractiveBartenderEnabled = interactiveEnabled);
                services.RemoveAll<ISpeechAuthorizationTokenService>();
                services.AddSingleton<ISpeechAuthorizationTokenService, FakeSpeechTokens>();
                services.RemoveAll<ILanguageContext>();
                services.AddSingleton<ILanguageContext>(new FixedLanguageContext(language));
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

    private sealed class FixedLanguageContext(string? currentLanguage) : ILanguageContext
    {
        public string? CurrentLanguage { get; } = currentLanguage;
        public bool HasLanguage => CurrentLanguage is not null;
        public IReadOnlyList<string> SupportedLanguages { get; } =
            ["Estonian", "German", "Polish", "Ukrainian"];

        public bool TrySetLanguage(string language) => false;

        public void Clear()
        {
        }
    }

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
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SpeakingSessionCreated(
                Guid.NewGuid(),
                avatar.Slug,
                avatar.Name,
                avatar.Voice,
                new SpeakingOpeningTurn(avatar.OpeningPolish, avatar.OpeningEnglish),
                avatar.Id == SpeakingAvatarId.Bartender
                    ? BartenderInteractionState.Create().ToSnapshot()
                    : null));
        }

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

        public Task<SpeakingTurn> SendActionAsync(
            Guid sessionId,
            string userId,
            SpeakingInteractionAction action,
            IReadOnlyDictionary<int, int>? denominations,
            CancellationToken cancellationToken = default)
        {
            if (action == SpeakingInteractionAction.SubmitPayment
                && denominations?.Any(item =>
                    !BartenderInteractionCatalog.Denominations.Contains(item.Key)
                    || item.Value <= 0) == true)
            {
                throw new SpeakingValidationException(
                    "The selected payment is not available in the wallet.");
            }

            return Task.FromResult(new SpeakingTurn
            {
                ReplyPolish = "Proszę bardzo.",
                ReplyEnglish = "There you are.",
                Coach = new SpeakingCoach(),
                Interaction = BartenderInteractionState.Create().ToSnapshot(),
            });
        }

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
