using System.Security.Claims;
using System.Text.Encodings.Web;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class ClassroomCallPresenceTests : IDisposable
{
    private const string TeacherId = "call-teacher-1";
    private const string StudentId = "call-student-1";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly Guid _classroomId = Guid.NewGuid();

    public ClassroomCallPresenceTests()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // The local dev config may point at a real ACS resource;
                    // tests must stay offline, so unconfigure video calling.
                    services.PostConfigure<Glosify.Services.Communication.AcsOptions>(options =>
                    {
                        options.Endpoint = null;
                        options.ConnectionString = null;
                    });
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

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        context.Users.AddRange(
            new ApplicationUser { Id = TeacherId, Email = "call-teacher@example.test", UserName = "call-teacher@example.test" },
            new ApplicationUser { Id = StudentId, Email = "call-student@example.test", UserName = "call-student@example.test" });
        context.Classrooms.Add(new Classroom
        {
            Id = _classroomId,
            OwnerUserId = TeacherId,
            Name = "Presence test",
            JoinCode = Guid.NewGuid().ToString("N")[..8],
            GroupCallId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.ClassroomMemberships.AddRange(
            new ClassroomMembership { Id = Guid.NewGuid(), ClassroomId = _classroomId, UserId = TeacherId, Role = ClassroomRole.Owner, JoinedAt = DateTimeOffset.UtcNow },
            new ClassroomMembership { Id = Guid.NewGuid(), ClassroomId = _classroomId, UserId = StudentId, Role = ClassroomRole.Student, JoinedAt = DateTimeOffset.UtcNow });
        context.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient CreateClient(string userId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        return client;
    }

    private HubConnection CreateHubConnection(string userId)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/classroom-chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Headers.Add(TestAuthHandler.UserHeader, userId);
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        var input = document.QuerySelector("input[name='__RequestVerificationToken']") as IHtmlInputElement
            ?? throw new Xunit.Sdk.XunitException($"No antiforgery token on {url}");
        return input.Value;
    }

    private async Task<System.Net.HttpStatusCode> PostCallTokenAsync(HttpClient client)
    {
        var token = await GetAntiforgeryTokenAsync(client, $"/Classroom/Call/{_classroomId}");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Classroom/CallToken?id={_classroomId}");
        request.Headers.Add("RequestVerificationToken", token);
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Details_HidesLiveBadge_WhenNoCallIsActive()
    {
        var client = CreateClient(TeacherId);

        var response = await client.GetAsync($"/Classroom/Details/{_classroomId}");
        response.EnsureSuccessStatusCode();
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());

        var badge = document.QuerySelector("[data-call-live]");
        Assert.NotNull(badge);
        Assert.True(badge!.HasAttribute("hidden"), "Live badge should be hidden when no call is active.");
        Assert.Equal("Video call", document.QuerySelector("[data-call-link-label]")?.TextContent.Trim());
    }

    [Fact]
    public async Task CallToken_ForbidsStudent_WhenNoCallIsActive()
    {
        var status = await PostCallTokenAsync(CreateClient(StudentId));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task CallToken_LetsTeacherStart_WhenNoCallIsActive()
    {
        var status = await PostCallTokenAsync(CreateClient(TeacherId));

        // The gate passed; ACS is unconfigured in tests, so token issuance
        // itself reports 503 rather than 403.
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, status);
    }

    [Fact]
    public async Task JoinCall_BroadcastsPresence_AndUnblocksStudents()
    {
        await using var teacher = CreateHubConnection(TeacherId);
        await using var student = CreateHubConnection(StudentId);

        var studentSawCall = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        student.On<CallChangedPayload>("callChanged", payload => studentSawCall.TrySetResult(payload.ParticipantCount));

        await teacher.StartAsync();
        await student.StartAsync();
        await teacher.InvokeAsync("JoinClassroom", _classroomId);
        await student.InvokeAsync("JoinClassroom", _classroomId);

        // Teacher reports joining the call: the student's page hears about it.
        await teacher.InvokeAsync("JoinCall", _classroomId);
        var count = await studentSawCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, count);

        // With a call in progress the student passes the teacher-only gate
        // (503 = ACS unconfigured, i.e. past the 403).
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, await PostCallTokenAsync(CreateClient(StudentId)));

        // The details page now renders the live badge.
        var response = await CreateClient(StudentId).GetAsync($"/Classroom/Details/{_classroomId}");
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        var badge = document.QuerySelector("[data-call-live]");
        Assert.NotNull(badge);
        Assert.False(badge!.HasAttribute("hidden"), "Live badge should be visible during a call.");
        Assert.Equal("1", document.QuerySelector("[data-call-live-count]")?.TextContent.Trim());
        Assert.Equal("Join call", document.QuerySelector("[data-call-link-label]")?.TextContent.Trim());

        // Leaving the call empties the roster and broadcasts zero.
        var studentSawEmpty = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        student.On<CallChangedPayload>("callChanged", payload =>
        {
            if (payload.ParticipantCount == 0)
            {
                studentSawEmpty.TrySetResult(0);
            }
        });
        await teacher.InvokeAsync("LeaveCall");
        await studentSawEmpty.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, await PostCallTokenAsync(CreateClient(StudentId)));
    }

    [Fact]
    public async Task Disconnect_RemovesCallPresence()
    {
        await using var watcher = CreateHubConnection(StudentId);
        var sawEmpty = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.On<CallChangedPayload>("callChanged", payload =>
        {
            if (payload.ParticipantCount == 0)
            {
                sawEmpty.TrySetResult(0);
            }
        });
        await watcher.StartAsync();
        await watcher.InvokeAsync("JoinClassroom", _classroomId);

        var teacher = CreateHubConnection(TeacherId);
        await teacher.StartAsync();
        await teacher.InvokeAsync("JoinClassroom", _classroomId);
        await teacher.InvokeAsync("JoinCall", _classroomId);

        // Dropping the connection (tab closed, network gone) must clear the
        // roster without an explicit LeaveCall.
        await teacher.StopAsync();
        await teacher.DisposeAsync();

        await sawEmpty.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed record CallChangedPayload(int ParticipantCount);

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string TestScheme = "Test";
        public const string UserHeader = "X-Test-User";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userId = userValues.ToString();
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Email, $"{userId}@example.test"),
                new Claim(ClaimTypes.Name, userId),
            };
            var identity = new ClaimsIdentity(claims, TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(identity.Name is null ? new ClaimsPrincipal() : new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
