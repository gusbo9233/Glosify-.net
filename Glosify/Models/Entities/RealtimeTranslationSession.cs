namespace Glosify.Models.Entities;

public sealed class RealtimeTranslationSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? SourceTranscriptionDeployment { get; set; }
    public string BillingModel { get; set; } = string.Empty;
    public int CreditsPerStartedMinute { get; set; }
    public Guid? TranscriptId { get; set; }
    public DateTimeOffset? TranscriptConsentAt { get; set; }
    public string Status { get; set; } = RealtimeTranslationSessionStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public int ChargedMinutes { get; set; }
    public int CreditsCharged { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<RealtimeTranslationMinute> Minutes { get; set; } = [];
    public RealtimeTranslationTranscript? Transcript { get; set; }
}

public static class RealtimeTranslationSessionStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Failed = "failed";
}
