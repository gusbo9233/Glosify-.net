using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant;

public sealed class AssistantAnalyticsOptions
{
    public const string SectionName = "AssistantAnalytics";
    public string LogAnalyticsWorkspaceResourceId { get; set; } = string.Empty;
    public int PurgeIntervalSeconds { get; set; } = 60;
    public List<string> PurgeTables { get; set; } =
    [
        "AppRequests", "AppDependencies", "AppTraces", "AppEvents", "AppExceptions", "AppGenAIContent",
    ];
}

internal sealed class AssistantTelemetryDeletionService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    IOptions<AssistantAnalyticsOptions> options,
    TimeProvider timeProvider,
    ILogger<AssistantTelemetryDeletionService> logger) : BackgroundService
{
    internal const string HttpClientName = "AssistantTelemetryPurge";
    private readonly AssistantAnalyticsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.LogAnalyticsWorkspaceResourceId))
        {
            logger.LogInformation(
                "Assistant telemetry deletion processing is disabled because no Log Analytics workspace resource id is configured.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.PurgeIntervalSeconds, 30, 3600));
        using var timer = new PeriodicTimer(interval, timeProvider);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Assistant telemetry deletion processing failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        var now = timeProvider.GetUtcNow();
        var pending = await context.AssistantTelemetryDeletionRequests
            .Where(request => request.Status == AssistantTelemetryDeletionStatus.Pending
                && request.NextAttemptAt <= now)
            .OrderBy(request => request.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var group in pending.GroupBy(request => new { request.TableName, request.DimensionName }))
        {
            try
            {
                var operationId = await SubmitPurgeAsync(
                    group.Key.TableName,
                    group.Key.DimensionName,
                    group.Select(request => request.DimensionValue).Distinct().ToArray(),
                    cancellationToken);
                foreach (var request in group)
                {
                    request.Status = AssistantTelemetryDeletionStatus.Submitted;
                    request.AzureOperationId = operationId;
                    request.CompletedAt = now;
                    request.LastError = null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var request in group)
                {
                    request.AttemptCount++;
                    request.LastError = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
                    request.NextAttemptAt = now.AddMinutes(Math.Min(60, Math.Pow(2, request.AttemptCount)));
                }
                logger.LogWarning(
                    ex,
                    "Could not submit assistant telemetry purge for {Count} correlation values.",
                    group.Count());
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> SubmitPurgeAsync(
        string tableName,
        string dimensionName,
        IReadOnlyList<string> dimensionValues,
        CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            cancellationToken);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var endpoint = $"{_options.LogAnalyticsWorkspaceResourceId.TrimEnd('/')}/purge?api-version=2025-07-01";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new
        {
            table = tableName,
            filters = new[]
            {
                new { column = dimensionName, @operator = "in", value = dimensionValues },
            },
        });
        using var response = await client.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Azure rejected the {tableName} purge request with {(int)response.StatusCode}: {responseText}");
        }

        return response.Headers.TryGetValues("x-ms-status-location", out var locations)
            ? locations.FirstOrDefault()
            : null;
    }
}
