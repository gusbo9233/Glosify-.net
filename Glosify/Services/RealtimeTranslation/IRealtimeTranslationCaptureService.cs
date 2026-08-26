namespace Glosify.Services.RealtimeTranslation;

public interface IRealtimeTranslationCaptureService
{
    Task<bool> IsAdminUserAsync(string userId, CancellationToken cancellationToken = default);

    Task AppendAsync(
        Guid sessionId,
        string userId,
        IReadOnlyList<CapturedRealtimeTranslationEvent> events,
        CancellationToken cancellationToken = default);
}

public sealed record CapturedRealtimeTranslationEvent(
    int Ordinal,
    int Sequence,
    string Stage,
    string Kind,
    string Text,
    string? SourceText,
    string? SourceLanguage,
    string? TargetLanguage,
    bool ProviderRequest,
    DateTimeOffset CapturedAt);
