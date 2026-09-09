namespace Glosify.Services.Speech;

public interface ITextToSpeechService
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string languageCode, CancellationToken cancellationToken = default);

    Task<Stream> GetOrSynthesizeAsync(
        string text,
        string languageCode,
        bool preferHighDefinition = false,
        string? voicePreference = null,
        CancellationToken cancellationToken = default);
}

public sealed record SpeechVoice(string ShortName, string DisplayName, string Locale);
