using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AdminAuthorizationTests
{
    private static readonly Guid AdminCaptureSessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LearnerCaptureSessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task AiCredits_RedirectsAnonymousUsersToLogin()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Admin/AiCredits");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AiCredits_ForbidsNonAdminEmail()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "learner@example.test");

        var response = await client.GetAsync("/Admin/AiCredits");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AiCredits_AllowsConfiguredAdminEmail()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "gusbo923@gmail.com");

        var response = await client.GetAsync("/Admin/AiCredits");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task TranslationCaptures_ForbidsNonAdminEmail()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "learner@example.test");

        var response = await client.GetAsync("/Admin/TranslationCaptures");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TranslationCaptures_DownloadsLatestOwnedSession()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "gusbo923@gmail.com");

        var response = await client.GetAsync("/Admin/TranslationCaptures");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            $"translation-capture-{AdminCaptureSessionId:N}.json",
            response.Content.Headers.ContentDisposition?.FileNameStar);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            AdminCaptureSessionId,
            json.RootElement.GetProperty("session").GetProperty("id").GetGuid());
        var captured = Assert.Single(json.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("translator", captured.GetProperty("stage").GetString());
        Assert.Equal("Hej världen", captured.GetProperty("text").GetString());
    }

    [Fact]
    public async Task TranslationCaptures_DoesNotReturnAnotherUsersSession()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "gusbo923@gmail.com");

        var response = await client.GetAsync($"/Admin/TranslationCaptures?sessionId={LearnerCaptureSessionId}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<GlosifyContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<GlosifyContext>>();
                    services.AddDbContext<GlosifyContext>(options => options.UseInMemoryDatabase(databaseName));
                    services
                        .AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme = TestAuthHandler.TestScheme;
                            options.DefaultChallengeScheme = TestAuthHandler.TestScheme;
                            options.DefaultForbidScheme = TestAuthHandler.TestScheme;
                        })
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.TestScheme, _ => { });
                });
            });

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        context.Users.AddRange(
            new ApplicationUser { Id = "admin-1", Email = "gusbo923@gmail.com", UserName = "gusbo923@gmail.com" },
            new ApplicationUser { Id = "learner-1", Email = "learner@example.test", UserName = "learner@example.test" });
        var capturedAt = DateTimeOffset.Parse("2026-08-26T18:00:00Z");
        context.RealtimeTranslationSessions.AddRange(
            CreateCaptureSession(AdminCaptureSessionId, "admin-1", capturedAt),
            CreateCaptureSession(LearnerCaptureSessionId, "learner-1", capturedAt.AddMinutes(1)));
        context.RealtimeTranslationCaptureEvents.AddRange(
            CreateCaptureEvent(AdminCaptureSessionId, "Hej världen", capturedAt),
            CreateCaptureEvent(LearnerCaptureSessionId, "Private learner text", capturedAt.AddMinutes(1)));
        context.SaveChanges();
        return factory;
    }

    private static RealtimeTranslationSession CreateCaptureSession(
        Guid sessionId,
        string userId,
        DateTimeOffset createdAt) => new()
        {
            Id = sessionId,
            UserId = userId,
            TargetLanguage = "sv",
            TranslationMode = "alternative",
            SpeechProvider = "elevenlabs",
            Model = "scribe_v2_realtime",
            BillingModel = "elevenlabs-scribe-v2-realtime+azure-translator-nmt",
            CreatedAt = createdAt,
            LastHeartbeatAt = createdAt,
            ExpiresAt = createdAt.AddMinutes(30),
        };

    private static RealtimeTranslationCaptureEvent CreateCaptureEvent(
        Guid sessionId,
        string text,
        DateTimeOffset capturedAt) => new()
        {
            SessionId = sessionId,
            Ordinal = 1,
            Sequence = 1,
            Stage = RealtimeTranslationCaptureStages.Translator,
            Kind = RealtimeTranslationCaptureKinds.Final,
            Text = text,
            SourceText = "Hello world",
            SourceLanguage = "en",
            TargetLanguage = "sv",
            ProviderRequest = true,
            CapturedAt = capturedAt,
            StoredAt = capturedAt,
        };

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string TestScheme = "Test";
        public const string EmailHeader = "X-Test-Email";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(EmailHeader, out var emailValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = emailValues.ToString();
            var id = string.Equals(email, "gusbo923@gmail.com", StringComparison.OrdinalIgnoreCase)
                ? "admin-1"
                : "learner-1";
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, id),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, email),
            };
            var identity = new ClaimsIdentity(claims, TestScheme);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
