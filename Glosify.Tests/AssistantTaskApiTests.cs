using System.Net;
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

public sealed class AssistantTaskApiTests
{
    [Fact]
    public async Task Bearer_start_returns_202_with_status_url_and_replays_same_submission()
    {
        using var factory = new TaskFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var input = new AssistantTaskStartInput("same-request", new("Add dom"));
        var response = await client.PostAsJsonAsync($"/api/assistant/tasks/chats/{factory.ThreadId}", input);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var created = await response.Content.ReadFromJsonAsync<AssistantTaskView>();
        var repeated = await client.PostAsJsonAsync($"/api/assistant/tasks/chats/{factory.ThreadId}", input);
        Assert.Equal(created!.Id, (await repeated.Content.ReadFromJsonAsync<AssistantTaskView>())!.Id);
        var status = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("queued", (await status.Content.ReadFromJsonAsync<AssistantTaskView>())!.Status);
    }

    [Fact]
    public async Task Foreign_chat_is_hidden_and_failure_uses_problem_details()
    {
        using var factory = new TaskFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync($"/api/assistant/tasks/chats/{factory.ForeignThreadId}", new AssistantTaskStartInput("foreign", new("Add dom")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(json.RootElement.GetProperty("detail").GetString(), json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cookie_mutations_require_antiforgery_and_large_input_is_validated()
    {
        using var factory = new TaskFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var web = await client.PostAsJsonAsync($"/Assistant/Tasks/chats/{factory.ThreadId}", new AssistantTaskStartInput("web", new("Add dom")));
        Assert.Equal(HttpStatusCode.BadRequest, web.StatusCode);
        var oversized = await client.PostAsJsonAsync($"/api/assistant/tasks/chats/{factory.ThreadId}", new AssistantTaskStartInput("large", new(new string('x', 50001))));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        var maximum = await client.PostAsJsonAsync($"/api/assistant/tasks/chats/{factory.ThreadId}", new AssistantTaskStartInput("maximum", new(new string('x', 50000))));
        Assert.Equal(HttpStatusCode.Accepted, maximum.StatusCode);
    }

    private sealed class TaskFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public Guid ThreadId { get; } = Guid.NewGuid();
        public Guid ForeignThreadId { get; } = Guid.NewGuid();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            connection.Open();
            var dbOptions = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
            using (var db = new GlosifyContext(dbOptions))
            {
                db.Database.EnsureCreated();
                db.Users.AddRange(new ApplicationUser { Id = "testuser" }, new ApplicationUser { Id = "foreign" });
                db.AssistantThreads.AddRange(new AssistantThread { Id = ThreadId, UserId = "testuser", Title = "Test" },
                    new AssistantThread { Id = ForeignThreadId, UserId = "foreign", Title = "Private" });
                db.SaveChanges();
            }
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<GlosifyContext>();
                services.AddScoped(_ => new GlosifyContext(dbOptions));
                services.PostConfigure<AssistantRuntimeOptions>(o => { o.Enabled = true; o.WebEnabled = true; o.CoverageEnabled = true; });
                services.AddTransient<AuthenticatedHandler>();
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    foreach (var scheme in o.Schemes) scheme.HandlerType = typeof(AuthenticatedHandler);
                });
            });
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) connection.Dispose(); }
    }
    private sealed class AuthenticatedHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "testuser")], Scheme.Name)), Scheme.Name)));
    }
}
