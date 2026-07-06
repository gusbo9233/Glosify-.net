namespace Glosify.Services.Ai;

public interface IImageTextExtractionService
{
    Task<string> ExtractTextAsync(
        string userId,
        Stream imageStream,
        string contentType,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
