using System.Text.Json;
using Glosify.Models.Entities;

namespace Glosify.Services.Game;

public sealed class GameUsageRate
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ServiceTier { get; set; } = "";
    public DateTimeOffset EffectiveFrom { get; set; }
    public decimal? InputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public decimal? CacheWritePerMillion { get; set; }
    public decimal Multiplier { get; set; } = 1;
    public string Source { get; set; } = "Configured account rate";
    public decimal? OutputPerMillion { get; set; }
    public decimal? AudioPerHour { get; set; }
    public decimal? CharactersPerThousand { get; set; }
}
public static class GameUsagePricing
{
    // Verified 2026-10-10. Conservative public list prices; promotional/subscription
    // adjustments are explicit deployment overrides, not assumed invoice discounts.
    public static IEnumerable<GameUsageRate> DefaultRates()
    {
        var effective = new DateTimeOffset(2026,10,10,0,0,0,TimeSpan.Zero);
        yield return new() {Provider="openai",Model="gpt-6-luna",Endpoint="decisions",EffectiveFrom=effective,InputPerMillion=.1m,Source="https://developers.openai.com/api/docs/guides/decisions"};
        foreach(var tier in new[]{"default","standard","fast","priority"}){
            var factor=tier is "fast" or "priority"?2m:1m;
            yield return new(){Provider="openai",Model="gpt-6-luna",Endpoint="responses",ServiceTier=tier,EffectiveFrom=effective,InputPerMillion=.1m*factor,CachedInputPerMillion=.01m*factor,CacheWritePerMillion=.125m*factor,OutputPerMillion=.5m*factor,Source="https://developers.openai.com/api/docs/pricing"};
        }
        yield return new(){Provider="elevenlabs",Model="scribe_v2_realtime",Endpoint="speech-to-text/realtime",EffectiveFrom=effective,AudioPerHour=.39m,Source="https://elevenlabs.io/pricing/api (public list rate)"};
        yield return new(){Provider="elevenlabs",Model="eleven_v4_turbo",Endpoint="text-to-dialogue/stream-input",EffectiveFrom=effective,CharactersPerThousand=.04m,Source="https://elevenlabs.io/pricing/api (regular list rate; excludes temporary promotion)"};
    }

    public static void Apply(GameUsageEvent usage, IEnumerable<GameUsageRate> rates)
    {
        usage.EstimatedUsd = null; usage.RateJson = null;
        if (usage.Measurement == "unknown") return;
        var rate = rates.Where(r => r.Provider == usage.Provider && r.Model == usage.Model && r.Endpoint == usage.Endpoint
            && r.ServiceTier == usage.ServiceTier && r.EffectiveFrom <= usage.StartedAt).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        if (rate is null) return;
        decimal? cost = null;
        if (usage.Endpoint == "decisions" && usage.InputTokens is { } decisionTokens && rate.InputPerMillion is { } decisionRate)
            cost = decisionTokens * decisionRate / 1_000_000m;
        else if (usage.InputTokens is { } input && usage.OutputTokens is { } output && usage.CachedInputTokens is { } cached
            && rate.InputPerMillion is { } inputRate && rate.OutputPerMillion is { } outputRate && rate.CachedInputPerMillion is { } cacheRate)
            cost = usage.CacheWriteTokens is > 0 && rate.CacheWritePerMillion is null ? null
                : (input - cached - (usage.CacheWriteTokens ?? 0)) * inputRate / 1_000_000m + cached * cacheRate / 1_000_000m
                    + (usage.CacheWriteTokens ?? 0) * (rate.CacheWritePerMillion ?? 0) / 1_000_000m + output * outputRate / 1_000_000m;
        else if (usage.AudioSeconds is { } seconds && rate.AudioPerHour is { } audioRate) cost = seconds * audioRate / 3600m;
        else if (usage.Characters is { } characters && rate.CharactersPerThousand is { } characterRate) cost = characters * characterRate / 1000m;
        if (cost is >= 0) { usage.EstimatedUsd = cost * rate.Multiplier; usage.RateJson = JsonSerializer.Serialize(rate); }
    }
}
