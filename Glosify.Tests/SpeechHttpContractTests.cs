using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Glosify.Services.Speech;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class SpeechHttpContractTests
{
    [Fact]
    public async Task Charged_speech_requires_POST_and_a_valid_antiforgery_token()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var body = new { text = "Hej", lang = "Swedish", voice = "sv-SE-SofieNeural", maxCredits = 1 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tts/voices?lang=Swedish")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Speech-Test-User", "speaker");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/tts?text=Hej&lang=Swedish")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/tts", body)).StatusCode);
        var token = await client.GetStringAsync("/_speech-test-token");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        var response = await client.PostAsJsonAsync("/api/tts", body);
        // The valid request reaches the actual action, which sees an unconfigured
        // provider. No Azure call or credit reservation is needed for this probe.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Speech service not configured", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(Action<SpeechOptions>? configure = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                if (configure is not null) services.Configure(configure);
                services.AddControllers().AddApplicationPart(typeof(SpeechTokenProbeController).Assembly);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "SpeechTest";
                    options.DefaultChallengeScheme = "SpeechTest";
                }).AddScheme<AuthenticationSchemeOptions, SpeechTestAuth>("SpeechTest", _ => { });
                services.RemoveAll<ITextToSpeechService>();
                services.AddSingleton<ITextToSpeechService, UnconfiguredSpeech>();
                services.RemoveAll<Glosify.Services.Ai.IPaidServiceGate>();
                services.AddSingleton<Glosify.Services.Ai.IPaidServiceGate, AlwaysAvailablePaidServiceGate>();
            });
        });
    }

    [Fact]
    public async Task Rate_conflicts_already_preserve_review_instructions_as_problem_details()
    {
        using var factory = CreateFactory(options => options.CreditsPerRequest = 2);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Speech-Test-User", "speaker");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", await client.GetStringAsync("/_speech-test-token"));
        var response = await client.PostAsJsonAsync("/api/tts", new { text = "Hej", lang = "Swedish", maxCredits = 1 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Speech pricing changed. Open speech settings again to review the price.", problem!.Detail);
    }

    [Theory]
    [InlineData(300, 250, HttpStatusCode.ServiceUnavailable)]
    [InlineData(300, 301, HttpStatusCode.BadRequest)]
    [InlineData(100, 101, HttpStatusCode.BadRequest)]
    public async Task Configured_text_limit_is_enforced_before_synthesis(int limit, int length, HttpStatusCode expected)
    {
        using var factory = CreateFactory(options => options.MaxTextLength = limit);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Speech-Test-User", "speaker");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", await client.GetStringAsync("/_speech-test-token"));
        var response = await client.PostAsJsonAsync("/api/tts", new { text = new string('a', length), lang = "Swedish", maxCredits = 1 });
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal($"text exceeds max length of {limit}.", problem!.Detail);
        }
    }

    private sealed class UnconfiguredSpeech : ITextToSpeechService
    {
        public bool IsConfigured => false;
        public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string languageCode, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpeechVoice>>([]);
        public Task<Stream> GetOrSynthesizeAsync(string text, string languageCode, bool preferHighDefinition = false, string? voicePreference = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Synthesis must not be reached.");
    }

    private sealed class SpeechTestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Speech-Test-User")) return Task.FromResult(AuthenticateResult.NoResult());
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "speaker")], "SpeechTest"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, "SpeechTest")));
        }
    }
}

[ApiController, Authorize]
public sealed class SpeechTokenProbeController(IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("/_speech-test-token")]
    public IActionResult Token() => Content(antiforgery.GetAndStoreTokens(HttpContext).RequestToken!);
}
