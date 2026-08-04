namespace Glosify.Models.Api;

public sealed record CreateRealtimeTranslationSessionRequest(
    string TargetLanguage,
    bool SaveTranscript = false,
    Guid? TranscriptId = null);

public sealed record SetRealtimeTranslationQuizLanguageRequest(string Code);

public sealed record RealtimeTranslationHeartbeatRequest(
    double? FirstCaptionLatencyMs = null,
    bool Reconnected = false);
