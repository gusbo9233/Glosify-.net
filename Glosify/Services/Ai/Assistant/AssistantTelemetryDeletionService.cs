using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
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

internal sealed class AssistantAnalyticsOptionsValidator : IValidateOptions<AssistantAnalyticsOptions>
{
    public ValidateOptionsResult Validate(string? name, AssistantAnalyticsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LogAnalyticsWorkspaceResourceId))
        {
            return ValidateOptionsResult.Success;
        }

        return AssistantTelemetryDeletionService.IsValidWorkspaceResourceId(
            options.LogAnalyticsWorkspaceResourceId)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AssistantAnalyticsOptions.SectionName}:LogAnalyticsWorkspaceResourceId must be an Azure Log Analytics workspace resource ID path.");
    }
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
    internal const int MaxAttempts = 5;
    private const string ApiVersion = "2025-07-01";
    private static readonly Uri ManagementEndpoint = new("https://management.azure.com");
    private static readonly TimeSpan SubmissionLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
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
                logger.LogError(
                    "Assistant telemetry deletion processing failed with {ErrorType}.",
                    ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        var now = timeProvider.GetUtcNow();

        await RecoverExpiredSubmissionLeasesAsync(context, now, cancellationToken);
        context.ChangeTracker.Clear();
        await SubmitPendingRequestsAsync(context, now, cancellationToken);
        await PollSubmittedRequestsAsync(context, now, cancellationToken);
    }

    private async Task RecoverExpiredSubmissionLeasesAsync(
        GlosifyContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = await context.AssistantTelemetryDeletionRequests
            .Where(request => request.Status == AssistantTelemetryDeletionStatus.Submitting
                && request.NextAttemptAt <= now)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var request in expired)
        {
            if (request.AttemptCount >= MaxAttempts)
            {
                MarkTerminalFailure(request, now, "Azure purge submission lease expired.");
            }
            else
            {
                request.Status = AssistantTelemetryDeletionStatus.Pending;
                request.LeaseId = null;
                request.NextAttemptAt = now;
                request.LastError = "Azure purge submission lease expired; the request will be retried.";
            }
        }

        if (expired.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SubmitPendingRequestsAsync(
        GlosifyContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = await context.AssistantTelemetryDeletionRequests
            .AsNoTracking()
            .Where(request => request.Status == AssistantTelemetryDeletionStatus.Pending
                && request.NextAttemptAt <= now)
            .OrderBy(request => request.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var group in pending.GroupBy(request => new { request.TableName, request.DimensionName }))
        {
            var claimed = await ClaimPendingRequestsAsync(
                context,
                group.Select(request => request.Id).ToArray(),
                now,
                cancellationToken);
            if (claimed.Count == 0)
            {
                continue;
            }

            try
            {
                var statusLocation = await SubmitPurgeAsync(
                    group.Key.TableName,
                    group.Key.DimensionName,
                    claimed.Select(request => request.DimensionValue).Distinct().ToArray(),
                    cancellationToken);
                foreach (var request in claimed)
                {
                    request.Status = AssistantTelemetryDeletionStatus.Submitted;
                    request.LeaseId = null;
                    request.AzureOperationId = statusLocation;
                    request.NextAttemptAt = now.Add(PollInterval);
                    request.CompletedAt = null;
                    request.LastError = null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var safeError = SafeError(ex);
                foreach (var request in claimed)
                {
                    request.LeaseId = null;
                    if (!IsTransient(ex) || request.AttemptCount >= MaxAttempts)
                    {
                        MarkTerminalFailure(request, now, safeError);
                    }
                    else
                    {
                        request.Status = AssistantTelemetryDeletionStatus.Pending;
                        request.NextAttemptAt = now.Add(RetryDelay(request.AttemptCount));
                        request.LastError = safeError;
                    }
                }
                logger.LogWarning(
                    "Could not submit assistant telemetry purge for {Count} correlation values: {Error}",
                    claimed.Count,
                    safeError);
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<List<AssistantTelemetryDeletionRequest>> ClaimPendingRequestsAsync(
        GlosifyContext context,
        IReadOnlyCollection<Guid> requestIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var claimId = Guid.NewGuid();
        var leaseExpiresAt = now.Add(SubmissionLease);

        if (context.Database.IsRelational())
        {
            await context.AssistantTelemetryDeletionRequests
                .Where(request => requestIds.Contains(request.Id)
                    && request.Status == AssistantTelemetryDeletionStatus.Pending
                    && request.NextAttemptAt <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(request => request.Status, AssistantTelemetryDeletionStatus.Submitting)
                    .SetProperty(request => request.LeaseId, claimId)
                    .SetProperty(request => request.AttemptCount, request => request.AttemptCount + 1)
                    .SetProperty(request => request.NextAttemptAt, leaseExpiresAt)
                    .SetProperty(request => request.LastError, (string?)null),
                    cancellationToken);
        }
        else
        {
            var candidates = await context.AssistantTelemetryDeletionRequests
                .Where(request => requestIds.Contains(request.Id)
                    && request.Status == AssistantTelemetryDeletionStatus.Pending
                    && request.NextAttemptAt <= now)
                .ToListAsync(cancellationToken);
            foreach (var request in candidates)
            {
                request.Status = AssistantTelemetryDeletionStatus.Submitting;
                request.LeaseId = claimId;
                request.AttemptCount++;
                request.NextAttemptAt = leaseExpiresAt;
                request.LastError = null;
            }
            await context.SaveChangesAsync(cancellationToken);
        }

        return await context.AssistantTelemetryDeletionRequests
            .Where(request => request.LeaseId == claimId)
            .ToListAsync(cancellationToken);
    }

    private async Task PollSubmittedRequestsAsync(
        GlosifyContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var submitted = await context.AssistantTelemetryDeletionRequests
            .Where(request => request.Status == AssistantTelemetryDeletionStatus.Submitted
                && request.NextAttemptAt <= now
                && request.AzureOperationId != null)
            .OrderBy(request => request.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var group in submitted.GroupBy(request => request.AzureOperationId!))
        {
            try
            {
                var status = await GetPurgeStatusAsync(group.Key, cancellationToken);
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var request in group)
                    {
                        request.Status = AssistantTelemetryDeletionStatus.Completed;
                        request.LeaseId = null;
                        request.CompletedAt = now;
                        request.LastError = null;
                    }
                }
                else if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var request in group)
                    {
                        request.NextAttemptAt = now.Add(PollInterval);
                        request.LastError = null;
                    }
                }
                else
                {
                    throw new TelemetryPurgeException(
                        "Azure returned an unsupported purge status.",
                        isTransient: false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var safeError = SafeError(ex);
                foreach (var request in group)
                {
                    request.AttemptCount++;
                    if (!IsTransient(ex) || request.AttemptCount >= MaxAttempts)
                    {
                        MarkTerminalFailure(request, now, safeError);
                    }
                    else
                    {
                        request.NextAttemptAt = now.Add(RetryDelay(request.AttemptCount));
                        request.LastError = safeError;
                    }
                }
                logger.LogWarning(
                    "Could not poll assistant telemetry purge status for {Count} correlation values: {Error}",
                    group.Count(),
                    safeError);
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<string> SubmitPurgeAsync(
        string tableName,
        string dimensionName,
        IReadOnlyList<string> dimensionValues,
        CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            cancellationToken);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var workspacePath = _options.LogAnalyticsWorkspaceResourceId.TrimEnd('/');
        var endpoint = new Uri(
            ManagementEndpoint,
            $"{workspacePath}/purge?api-version={ApiVersion}");
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
        if (!response.IsSuccessStatusCode)
        {
            throw HttpFailure("submit", response.StatusCode);
        }

        var statusLocation = response.Headers.TryGetValues("x-ms-status-location", out var locations)
            ? locations.FirstOrDefault()
            : null;
        return NormalizeStatusLocation(statusLocation, workspacePath);
    }

    private async Task<string> GetPurgeStatusAsync(
        string statusLocation,
        CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            cancellationToken);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(ManagementEndpoint, statusLocation));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpFailure("poll", response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<PurgeStatusResponse>(
            cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(result?.Status)
            ? throw new TelemetryPurgeException(
                "Azure returned an empty purge status.",
                isTransient: false)
            : result.Status;
    }

    private static TelemetryPurgeException HttpFailure(string operation, HttpStatusCode statusCode) =>
        new(
            $"Azure purge {operation} failed with HTTP {(int)statusCode}.",
            statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)statusCode >= 500);

    private static string NormalizeStatusLocation(string? statusLocation, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(statusLocation)
            || !Uri.TryCreate(ManagementEndpoint, statusLocation, out var absolute)
            || absolute.Scheme != Uri.UriSchemeHttps
            || !string.Equals(absolute.Host, ManagementEndpoint.Host, StringComparison.OrdinalIgnoreCase)
            || !absolute.AbsolutePath.StartsWith(
                $"{workspacePath}/operations/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new TelemetryPurgeException(
                "Azure returned an invalid purge status location.",
                isTransient: false);
        }

        var relative = absolute.PathAndQuery;
        if (relative.Length > 512)
        {
            throw new TelemetryPurgeException(
                "Azure returned an oversized purge status location.",
                isTransient: false);
        }

        return relative;
    }

    internal static bool IsValidWorkspaceResourceId(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)
            || !resourceId.StartsWith("/", StringComparison.Ordinal)
            || resourceId.Contains('?')
            || resourceId.Contains('#')
            || resourceId.Contains('\\')
            || resourceId.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = resourceId.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 8
            && string.Equals(segments[0], "subscriptions", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "resourceGroups", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[4], "providers", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[5], "Microsoft.OperationalInsights", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[6], "workspaces", StringComparison.OrdinalIgnoreCase)
            && segments[1].Length > 0
            && segments[3].Length > 0
            && segments[7].Length > 0;
    }

    private static void MarkTerminalFailure(
        AssistantTelemetryDeletionRequest request,
        DateTimeOffset now,
        string error)
    {
        request.Status = AssistantTelemetryDeletionStatus.Failed;
        request.LeaseId = null;
        request.CompletedAt = now;
        request.LastError = Truncate(error, 2000);
    }

    private static bool IsTransient(Exception exception) =>
        exception is not TelemetryPurgeException purge || purge.IsTransient;

    private static string SafeError(Exception exception) => Truncate(exception switch
    {
        TelemetryPurgeException purge => purge.Message,
        OperationCanceledException => "Azure purge operation was cancelled.",
        HttpRequestException => "Azure purge endpoint could not be reached.",
        Azure.Identity.AuthenticationFailedException => "Azure authentication failed for the purge operation.",
        _ => $"Azure purge operation failed with {exception.GetType().Name}.",
    }, 2000);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static TimeSpan RetryDelay(int attemptCount) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(1, attemptCount))));

    private sealed record PurgeStatusResponse(
        [property: JsonPropertyName("status")] string Status);

    private sealed class TelemetryPurgeException(string message, bool isTransient) : Exception(message)
    {
        public bool IsTransient { get; } = isTransient;
    }
}
