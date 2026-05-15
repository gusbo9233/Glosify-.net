namespace Glosify.Services;

public interface IImageTextExtractionService
{
    Task<string> ExtractTextAsync(
        Stream imageStream,
        string contentType,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
