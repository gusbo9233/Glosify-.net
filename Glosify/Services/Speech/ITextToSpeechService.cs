namespace Glosify.Services.Speech;

public interface ITextToSpeechService
{
    bool IsConfigured { get; }

    Task<Stream> GetOrSynthesizeAsync(string text, string languageCode, CancellationToken cancellationToken = default);
}
