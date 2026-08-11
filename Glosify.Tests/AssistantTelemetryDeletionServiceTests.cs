using System.Net;
using System.Text;
using Azure.Core;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantTelemetryDeletionServiceTests
{
    private const string WorkspacePath =
        "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws";
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accepted_purge_is_polled_and_only_completed_after_azure_reports_completion()
    {
        var clock = new FakeTimeProvider(Origin);
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add(
                    "x-ms-status-location",
                    $"https://management.azure.com{WorkspacePath}/operations/op-1?api-version=2025-07-01");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"completed"}""", Encoding.UTF8, "application/json"),
            };
        });
        await using var services = CreateServices(out var scopeFactory);
        await SeedAsync(services, NewRequest(clock.GetUtcNow()));
        using var clients = new StubHttpClientFactory(handler);
        var worker = CreateWorker(scopeFactory, clients, clock);

        await worker.ProcessBatchAsync(default);

        var submitted = await LoadSingleAsync(services);
        Assert.Equal(AssistantTelemetryDeletionStatus.Submitted, submitted.Status);
        Assert.Null(submitted.CompletedAt);
        Assert.Equal(1, submitted.AttemptCount);
        Assert.StartsWith($"{WorkspacePath}/operations/", submitted.AzureOperationId);
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
        Assert.Equal("management.azure.com", handler.Requests[0].Uri.Host);

        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.ProcessBatchAsync(default);

        var completed = await LoadSingleAsync(services);
        Assert.Equal(AssistantTelemetryDeletionStatus.Completed, completed.Status);
        Assert.Equal(clock.GetUtcNow(), completed.CompletedAt);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal(HttpMethod.Post, request.Method),
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("management.azure.com", request.Uri.Host);
                Assert.Contains("/operations/op-1", request.Uri.AbsolutePath);
            });
    }

    [Fact]
    public async Task Permanent_or_exhausted_submission_failure_is_terminal_and_does_not_store_response_content()
    {
        var clock = new FakeTimeProvider(Origin);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("apiKey=must-not-be-stored"),
        });
        await using var services = CreateServices(out var scopeFactory);
        var request = NewRequest(clock.GetUtcNow());
        request.AttemptCount = AssistantTelemetryDeletionService.MaxAttempts - 1;
        await SeedAsync(services, request);
        using var clients = new StubHttpClientFactory(handler);
        var worker = CreateWorker(scopeFactory, clients, clock);

        await worker.ProcessBatchAsync(default);

        var failed = await LoadSingleAsync(services);
        Assert.Equal(AssistantTelemetryDeletionStatus.Failed, failed.Status);
        Assert.Equal(AssistantTelemetryDeletionService.MaxAttempts, failed.AttemptCount);
        Assert.Equal(clock.GetUtcNow(), failed.CompletedAt);
        Assert.Contains("HTTP 500", failed.LastError);
        Assert.DoesNotContain("must-not-be-stored", failed.LastError);
    }

    [Fact]
    public async Task Foreign_status_location_is_rejected_without_polling_or_persisting_it()
    {
        var clock = new FakeTimeProvider(Origin);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.Add(
                "x-ms-status-location",
                "https://attacker.example/operations/op-1?api-version=2025-07-01");
            return response;
        });
        await using var services = CreateServices(out var scopeFactory);
        await SeedAsync(services, NewRequest(clock.GetUtcNow()));
        using var clients = new StubHttpClientFactory(handler);
        var worker = CreateWorker(scopeFactory, clients, clock);

        await worker.ProcessBatchAsync(default);

        var failed = await LoadSingleAsync(services);
        Assert.Equal(AssistantTelemetryDeletionStatus.Failed, failed.Status);
        Assert.Null(failed.AzureOperationId);
        Assert.Contains("invalid purge status location", failed.LastError);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(WorkspacePath, true)]
    [InlineData("", true)]
    [InlineData("https://attacker.example/workspaces/ws", false)]
    [InlineData("/subscriptions/sub/resourceGroups/rg/providers/Other/workspaces/ws", false)]
    [InlineData("/subscriptions/sub/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws?x=1", false)]
    public void Options_validation_accepts_only_disabled_or_workspace_resource_path(
        string workspaceResourceId,
        bool valid)
    {
        var result = new AssistantAnalyticsOptionsValidator().Validate(
            null,
            new AssistantAnalyticsOptions
            {
                LogAnalyticsWorkspaceResourceId = workspaceResourceId,
            });

        Assert.Equal(valid, result.Succeeded);
    }

    private static AssistantTelemetryDeletionService CreateWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory clients,
        TimeProvider clock) => new(
            scopeFactory,
            clients,
            new StubTokenCredential(),
            Options.Create(new AssistantAnalyticsOptions
            {
                LogAnalyticsWorkspaceResourceId = WorkspacePath,
            }),
            clock,
            NullLogger<AssistantTelemetryDeletionService>.Instance);

    private static ServiceProvider CreateServices(out IServiceScopeFactory scopeFactory)
    {
        var root = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<GlosifyContext>(options =>
            options.UseInMemoryDatabase(databaseName, root));
        var provider = services.BuildServiceProvider();
        scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return provider;
    }

    private static AssistantTelemetryDeletionRequest NewRequest(DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TableName = "AppTraces",
        DimensionName = "OperationId",
        DimensionValue = "0123456789abcdef0123456789abcdef",
        Status = AssistantTelemetryDeletionStatus.Pending,
        NextAttemptAt = now,
        CreatedAt = now,
    };

    private static async Task SeedAsync(
        ServiceProvider services,
        AssistantTelemetryDeletionRequest request)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        context.AssistantTelemetryDeletionRequests.Add(request);
        await context.SaveChangesAsync();
    }

    private static async Task<AssistantTelemetryDeletionRequest> LoadSingleAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GlosifyContext>()
            .AssistantTelemetryDeletionRequests
            .AsNoTracking()
            .SingleAsync();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
            handler.Dispose();
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri);

    private sealed class StubTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token = new(
            "test-token",
            DateTimeOffset.UtcNow.AddHours(1));

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }
}
