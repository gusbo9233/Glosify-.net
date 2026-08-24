using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Glosify.Models.Api;

public sealed record AssistantChatInput(
    Guid? ContextQuizId,
    Guid? ContextTranscriptId = null,
    Guid? ContextBookDocumentId = null);

public sealed record AssistantSendInput(
    [param: Required, StringLength(8000)] string Message,
    Guid? ContextQuizId,
    string? FocusedWordId,
    Guid? DocumentId,
    int? PageNumber,
    Guid? CustomQuizId,
    Guid? TranscriptId = null,
    Guid? BookDocumentId = null);

public sealed record AssistantFeedbackInput(
    [param: Required] string Rating,
    IReadOnlyList<string>? ReasonCodes,
    [param: StringLength(1000)] string? Comment);

public sealed record AssistantClientMetricsInput(
    [property: JsonRequired]
    [param: Range(0, 900000)] double ClientDurationMs);
