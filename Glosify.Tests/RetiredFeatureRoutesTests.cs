using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Authentication;
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

public sealed class RetiredFeatureRoutesTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public RetiredFeatureRoutesTests()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<GlosifyContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<GlosifyContext>>();
                services.AddDbContext<GlosifyContext>(options => options.UseInMemoryDatabase(databaseName));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.TestScheme;
                        options.DefaultChallengeScheme = TestAuthHandler.TestScheme;
                        options.DefaultForbidScheme = TestAuthHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.TestScheme, _ => { });
            });
        });
    }

    [Theory]
    [InlineData("/Speaking")]
    [InlineData("/api/speaking/speech-token")]
    [InlineData("/api/speaking/sessions")]
    [InlineData("/Classroom")]
    [InlineData("/Classroom/00000000-0000-0000-0000-000000000001")]
    [InlineData("/hubs/classroom-chat")]
    [InlineData("/CustomQuizzes/00000000-0000-0000-0000-000000000001/Edit")]
    [InlineData("/CustomQuizzes/00000000-0000-0000-0000-000000000001/Play")]
    public async Task RetiredRoutesReturnNatural404ForFormerProductionAdministrators(string route)
    {
        using var client = CreateAdminClient();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static TheoryData<HttpMethod, string> RetiredCustomQuizMutations => new()
    {
        { HttpMethod.Post, "/Quizzes/00000000-0000-0000-0000-000000000001/Custom/New" },
        { HttpMethod.Post, "/CustomQuizzes" },
        { HttpMethod.Put, "/CustomQuizzes/00000000-0000-0000-0000-000000000001" },
        { HttpMethod.Delete, "/CustomQuizzes/00000000-0000-0000-0000-000000000001" },
        { HttpMethod.Post, "/CustomQuizzes/00000000-0000-0000-0000-000000000001/Grade" },
    };

    [Theory]
    [MemberData(nameof(RetiredCustomQuizMutations))]
    public async Task RetiredCustomQuizMutationRoutesReturnNatural404(
        HttpMethod method,
        string route)
    {
        using var client = CreateAdminClient();
        using var request = new HttpRequestMessage(method, route)
        {
            Content = JsonContent.Create(new { }),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QuizAndExploreDetailsContainNoCustomQuizSurface()
    {
        var quizId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
            db.Quizzes.Add(new Quiz
            {
                Id = quizId,
                UserId = "admin-1",
                Name = "Retirement coverage",
                SourceLanguage = "English",
                TargetLanguage = "Polish",
                Language = "Polish",
                IsPublic = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = CreateAdminClient();
        foreach (var route in new[] { $"/Quiz/Details/{quizId}", $"/Explore/Details/{quizId}" })
        {
            var response = await client.GetAsync(route);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("CustomQuizzes", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("custom quiz", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data-custom-quiz", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AuthenticatedHomeAndNavigationAreQuizFirstAndContainNoRetiredSurface()
    {
        using var client = CreateAdminClient();
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var document = await new HtmlParser().ParseDocumentAsync(html);

        var heroActions = document.QuerySelectorAll(".home-hero-actions a");
        Assert.Equal(2, heroActions.Length);
        Assert.Contains("/Quiz", heroActions[0].GetAttribute("href"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Books", heroActions[1].GetAttribute("href"), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(document.QuerySelector(".home-action-card-anki[href*='/Anki']"));
        Assert.NotNull(document.QuerySelector(".home-feature-card-books"));
        Assert.NotNull(document.QuerySelector(".home-feature-card-explore"));

        Assert.DoesNotContain("/Speaking", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Classroom", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-speaking-unavailable", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-classroom-unavailable", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("custom quiz", html, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "gusbo923@gmail.com");
        client.DefaultRequestHeaders.Add("Cookie", "glosify.language=Polish");
        return client;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "RetiredFeaturesTest";
        public const string EmailHeader = "X-Test-Email";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var email = Request.Headers[EmailHeader].ToString();
            if (string.IsNullOrWhiteSpace(email))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, email),
            ], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
