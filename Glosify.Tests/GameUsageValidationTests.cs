using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class GameUsageValidationTests
{
    [Theory]
    [InlineData(100L, long.MaxValue, long.MaxValue)]
    [InlineData(100L, long.MaxValue, 0L)]
    [InlineData(100L, 0L, long.MaxValue)]
    [InlineData(null, long.MaxValue, long.MaxValue)]
    [InlineData(null, 100_000_001L, 0L)]
    [InlineData(null, 0L, 100_000_001L)]
    [InlineData(100L, -1L, 0L)]
    [InlineData(100L, 0L, -1L)]
    [InlineData(null, -1L, null)]
    [InlineData(null, null, -1L)]
    [InlineData(100L, 101L, 0L)]
    [InlineData(100L, 0L, 101L)]
    [InlineData(100L, 60L, 41L)]
    [InlineData(0L, 1L, 0L)]
    [InlineData(100_000_000L, 60_000_000L, 40_000_001L)]
    public async Task Invalid_cache_counts_return_problem_details_without_persisting_usage(
        long? inputTokens, long? cachedTokens, long? cacheWriteTokens)
    {
        using var fixture = new GameAccessTests.Fixture();
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Add("X-Game-Service-Key", new string('x', 40));
        var usage = CreateUsage(inputTokens, cachedTokens, cacheWriteTokens);

        using var response = await client.PostAsJsonAsync("/api/game/internal/usage", usage);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("bad_request", problem.GetProperty("code").GetString());
        Assert.Equal("/api/game/internal/usage", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("error").GetString()));
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var scope = fixture.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        Assert.Empty(await db.Set<GameUsageEvent>().ToListAsync());
        Assert.Empty(await db.Set<GamePlaySession>().ToListAsync());
    }

    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(0L, 0L, 0L)]
    [InlineData(100L, 60L, 40L)]
    [InlineData(100_000_000L, 100_000_000L, 0L)]
    [InlineData(100_000_000L, 0L, 100_000_000L)]
    public async Task Valid_cache_counts_at_the_total_and_limit_are_persisted(
        long inputTokens, long? cachedTokens, long? cacheWriteTokens)
    {
        using var fixture = new GameAccessTests.Fixture();
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Add("X-Game-Service-Key", new string('x', 40));
        var usage = CreateUsage(inputTokens, cachedTokens, cacheWriteTokens);

        using var response = await client.PostAsJsonAsync("/api/game/internal/usage", usage);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = fixture.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        var stored = Assert.Single(await db.Set<GameUsageEvent>().ToListAsync());
        Assert.Equal(usage.Id, stored.Id);
        Assert.Equal(inputTokens, stored.InputTokens);
        Assert.Equal(cachedTokens, stored.CachedInputTokens);
        Assert.Equal(cacheWriteTokens, stored.CacheWriteTokens);
        Assert.Equal(usage.SessionId, Assert.Single(await db.Set<GamePlaySession>().ToListAsync()).Id);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, 0L)]
    [InlineData(100L, null)]
    [InlineData(null, 100L)]
    [InlineData(100_000_000L, 100_000_000L)]
    public async Task Unknown_input_totals_preserve_bounded_partial_counts_without_pricing(
        long? cachedTokens, long? cacheWriteTokens)
    {
        using var fixture = new GameAccessTests.Fixture();
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Add("X-Game-Service-Key", new string('x', 40));
        var usage = CreateUsage(null, cachedTokens, cacheWriteTokens);
        usage.Measurement = "unknown";

        using var response = await client.PostAsJsonAsync("/api/game/internal/usage", usage);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = fixture.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        var stored = Assert.Single(await db.Set<GameUsageEvent>().ToListAsync());
        Assert.Null(stored.InputTokens);
        Assert.Equal(cachedTokens, stored.CachedInputTokens);
        Assert.Equal(cacheWriteTokens, stored.CacheWriteTokens);
        Assert.Equal("unknown", stored.Measurement);
        Assert.Null(stored.EstimatedUsd);
        Assert.Null(stored.RateJson);
    }

    private static GameUsageEvent CreateUsage(long? inputTokens, long? cachedTokens, long? cacheWriteTokens) => new()
    {
        Id = Guid.NewGuid(),
        UserId = "admin",
        SessionId = Guid.NewGuid().ToString("N"),
        Provider = "openai",
        Model = "gpt-6-luna",
        Endpoint = "decisions",
        Outcome = "completed",
        Measurement = "actual",
        InputTokens = inputTokens,
        OutputTokens = 0,
        CachedInputTokens = cachedTokens,
        CacheWriteTokens = cacheWriteTokens,
        StartedAt = DateTimeOffset.UtcNow,
    };
}
