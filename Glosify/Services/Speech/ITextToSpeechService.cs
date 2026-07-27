namespace Glosify.Services.Speech;

public interface ITextToSpeechService
{
    bool IsConfigured { get; }

    Task<Stream> GetOrSynthesizeAsync(
        string text,
        string languageCode,
        bool preferHighDefinition = false,
        string? voicePreference = null,
        CancellationToken cancellationToken = default);
}
