namespace Glosify.Services.RealtimeTranslation;

public static class RealtimeTranslationConstants
{
    public const string Provider = "foundry";
}

public sealed record RealtimeTranslationLanguage(string Code, string Name);
public sealed record RealtimeTranslationSelectedQuizLanguage(string Code, string Name);

public sealed record RealtimeTranslationCatalog(
    IReadOnlyList<RealtimeTranslationLanguage> Languages,
    IReadOnlyList<RealtimeTranslationLanguage> QuizLanguages,
    int CreditsPerMinute,
    int SavedTranscriptCreditsPerMinute,
    bool SavedSourceTranscriptsEnabled,
    RealtimeTranslationSelectedQuizLanguage? SelectedQuizLanguage,
    int MaxSessionMinutes,
    int RenewalLeadSeconds,
    int HeartbeatSeconds,
    string Model,
    int AvailableCredits);

public sealed record RealtimeTranslationSessionCreated(
    Guid SessionId,
    string RelayToken,
    DateTimeOffset RelayTokenExpiresAt,
    string RelayPath,
    int MinuteIndex,
    int AvailableCredits,
    int CreditsPerMinute,
    Guid? TranscriptId);

public sealed record RealtimeTranslationMinuteResult(
    Guid SessionId,
    int MinuteIndex,
    string Status,
    int AvailableCredits,
    int ChargedMinutes,
    int CreditsCharged);

public sealed record RealtimeTranslationSessionStatus(
    Guid SessionId,
    string Status,
    DateTimeOffset ExpiresAt,
    int ChargedMinutes,
    int CreditsCharged);

public sealed record RealtimeTranslationRelayGrant(
    string Token,
    DateTimeOffset ExpiresAt);

public sealed record RealtimeTranslationRelayAuthorization(
    Guid SessionId,
    string UserId,
    string TargetLanguage,
    bool SaveTranscript);

public sealed class RealtimeTranslationValidationException(string message) : InvalidOperationException(message);
public sealed class RealtimeTranslationConflictException(string message) : InvalidOperationException(message);
public sealed class RealtimeTranslationNotFoundException(string message) : InvalidOperationException(message);
public sealed class RealtimeTranslationExpiredException(string message) : InvalidOperationException(message);
public sealed class RealtimeTranslationUnavailableException(string message) : InvalidOperationException(message);
public sealed class RealtimeTranslationUpstreamException(string message) : InvalidOperationException(message);
