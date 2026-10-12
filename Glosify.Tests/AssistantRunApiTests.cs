using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantRunApiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Bearer_start_returns_202_with_a_status_url_and_replays_the_same_submission()
    {
        using var factory = new RunFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var input = new AssistantRunStartInput("same-request", new AssistantRunInput("Add dom"));

        var response = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", input);
        var repeated = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", input);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AssistantRunView>(Web);
        Assert.Equal(created!.Id, (await repeated.Content.ReadFromJsonAsync<AssistantRunView>(Web))!.Id);
        var status = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(AssistantRunStatus.Queued, (await status.Content.ReadFromJsonAsync<AssistantRunView>(Web))!.Status);
        var latest = await client.GetFromJsonAsync<AssistantRunView>($"/api/assistant/runs/chats/{factory.ThreadId}", Web);
        Assert.Equal(created.Id, latest!.Id);
    }

    [Fact]
    public async Task A_foreign_chat_is_hidden_and_the_failure_uses_problem_details()
    {
        using var factory = new RunFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var response = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ForeignThreadId}", new AssistantRunStartInput("foreign", new AssistantRunInput("Add dom")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(json.RootElement.GetProperty("detail").GetString(), json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_second_request_while_one_runs_is_a_conflict()
    {
        using var factory = new RunFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", new AssistantRunStartInput("first", new AssistantRunInput("Add dom")));

        var second = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", new AssistantRunStartInput("second", new AssistantRunInput("Add kot")));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cookie_mutations_require_antiforgery_and_input_size_is_validated()
    {
        using var factory = new RunFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var web = await client.PostAsJsonAsync($"/Assistant/Runs/chats/{factory.ThreadId}", new AssistantRunStartInput("web", new AssistantRunInput("Add dom")));
        var oversized = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", new AssistantRunStartInput("large", new AssistantRunInput(new string('x', 50_001))));
        var maximum = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", new AssistantRunStartInput("maximum", new AssistantRunInput(new string('x', 50_000))));

        Assert.Equal(HttpStatusCode.BadRequest, web.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, maximum.StatusCode);
    }

    [Fact]
    public async Task Events_stream_the_run_as_server_sent_events_with_its_revision_as_the_id()
    {
        using var factory = new RunFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var start = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", new AssistantRunStartInput("events", new AssistantRunInput("Hello")));
        var run = (await start.Content.ReadFromJsonAsync<AssistantRunView>(Web))!;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/assistant/runs/{run.Id}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line && line.Length > 0)
        {
            lines.Add(line);
        }

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: run", lines);
        Assert.Contains($"id: {run.Revision}", lines);
        var data = JsonDocument.Parse(lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]).RootElement;
        Assert.Equal(run.Id, data.GetProperty("id").GetGuid());
        Assert.Equal("queued", data.GetProperty("status").GetString());

        var missing = await client.GetAsync($"/api/assistant/runs/{Guid.NewGuid()}/events");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Stop_and_a_stale_decision_answer_with_the_current_state_or_a_conflict()
    {
        using var factory = new RunFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var start = await client.PostAsJsonAsync($"/api/assistant/runs/chats/{factory.ThreadId}", new AssistantRunStartInput("stop", new AssistantRunInput("Hello")));
        var run = (await start.Content.ReadFromJsonAsync<AssistantRunView>(Web))!;

        var approve = await client.PostAsJsonAsync($"/api/assistant/runs/{run.Id}/approve", new AssistantRunCommand(run.Revision));
        var stop = await client.PostAsJsonAsync($"/api/assistant/runs/{run.Id}/cancel", new AssistantRunCommand(run.Revision - 1));
        var undo = await client.PostAsync($"/api/assistant/runs/{run.Id}/undo", null);

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Equal(AssistantRunStatus.Cancelled, (await stop.Content.ReadFromJsonAsync<AssistantRunView>(Web))!.Status);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
    }

    private sealed class RunFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public Guid ThreadId { get; } = Guid.NewGuid();
        public Guid ForeignThreadId { get; } = Guid.NewGuid();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(_connection).Options;
            using (var db = new GlosifyContext(options))
            {
                db.Database.EnsureCreated();
                db.Users.AddRange(new ApplicationUser { Id = "testuser" }, new ApplicationUser { Id = "foreign" });
                db.AssistantThreads.AddRange(
                    new AssistantThread { Id = ThreadId, UserId = "testuser", Title = "Test" },
                    new AssistantThread { Id = ForeignThreadId, UserId = "foreign", Title = "Private" });
                db.SaveChanges();
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<GlosifyContext>();
                services.AddScoped(_ => new GlosifyContext(options));
                services.AddTransient<AuthenticatedHandler>();
                services.PostConfigure<AuthenticationOptions>(authentication =>
                {
                    foreach (var scheme in authentication.Schemes)
                    {
                        scheme.HandlerType = typeof(AuthenticatedHandler);
                    }
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }

    private sealed class AuthenticatedHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "testuser")], Scheme.Name)),
                Scheme.Name)));
    }
}
