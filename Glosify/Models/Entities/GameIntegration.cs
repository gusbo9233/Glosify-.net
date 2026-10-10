namespace Glosify.Models.Entities;

public sealed class GamePlayerProfile
{
    public string UserId { get; set; } = "";
    public string CharacterJson { get; set; } = "{}";
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GameUsageEvent
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ServiceTier { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Measurement { get; set; } = "unknown";
    public long? InputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public long? OutputTokens { get; set; }
    public decimal? AudioSeconds { get; set; }
    public long? Characters { get; set; }
    public decimal? EstimatedUsd { get; set; }
    public string? RateJson { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class GamePlaySession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public long ActiveSeconds { get; set; }
}
