using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Api;

public sealed record CreateRealtimeTranslationSessionRequest(
    [param: Required, StringLength(64)] string TargetLanguage,
    bool SaveTranscript = false,
    Guid? TranscriptId = null,
    [param: StringLength(16)] string? TranslationMode = null,
    [param: StringLength(16)] string? SourceLanguage = null);

public sealed record SetRealtimeTranslationQuizLanguageRequest(
    [param: Required, StringLength(16)] string Code);

public sealed record RealtimeTranslationHeartbeatRequest(
    [param: Range(0, 300_000)] double? FirstCaptionLatencyMs = null,
    bool Reconnected = false);
