using Glosify.Models;

namespace Glosify.Services;

public interface IWordDetailEnrichmentService
{
    Task<bool> EnrichAsync(
        WordDetail detail,
        Word? word,
        Quiz? quiz,
        string fallbackWord,
        string fallbackTargetLanguage,
        CancellationToken cancellationToken = default,
        bool force = false);

    bool ApplyGenerated(WordDetail detail, GeneratedWordDetail generated, bool force = false);
}
