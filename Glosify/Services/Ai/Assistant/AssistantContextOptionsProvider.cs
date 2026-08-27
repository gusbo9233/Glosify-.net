using Glosify.Models.ViewModels;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Builds the small projections used by the assistant's context pickers. Each source is
/// best-effort so one unavailable library does not hide the others.
/// </summary>
public sealed class AssistantContextOptionsProvider
{
    private const int TranscriptPageSize = 50;

    private readonly IQuizService _quizzes;
    private readonly IBookDocumentService _books;
    private readonly IRealtimeTranslationTranscriptService _transcripts;
    private readonly IQuizLanguagePreferenceService _languagePreferences;
    private readonly ILogger<AssistantContextOptionsProvider> _logger;

    public AssistantContextOptionsProvider(
        IQuizService quizzes,
        IBookDocumentService books,
        IRealtimeTranslationTranscriptService transcripts,
        IQuizLanguagePreferenceService languagePreferences,
        ILogger<AssistantContextOptionsProvider> logger)
    {
        _quizzes = quizzes;
        _books = books;
        _transcripts = transcripts;
        _languagePreferences = languagePreferences;
        _logger = logger;
    }

    public async Task<AssistantContextOptions> GetAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        // The production readers share the request's scoped GlosifyContext. Keep these
        // best-effort queries sequential: EF Core does not permit concurrent operations
        // on one DbContext instance.
        var quizzes = await TryLoadAsync(
            "quizzes",
            async () => (IReadOnlyList<AssistantQuizOption>)(await _quizzes
                .GetUserQuizzesAsync(userId, cancellationToken))
                .Select(quiz => new AssistantQuizOption(
                    quiz.Id,
                    quiz.Name,
                    quiz.SourceLanguage,
                    quiz.TargetLanguage))
                .ToArray(),
            cancellationToken);

        var books = await TryLoadAsync(
            "books",
            async () => (IReadOnlyList<AssistantMaterialOption>)(await _books
                .GetUserBooksAsync(userId, cancellationToken))
                .Select(book => new AssistantMaterialOption(book.Id, book.Title))
                .ToArray(),
            cancellationToken);

        var transcripts = await TryLoadAsync(
            "transcripts",
            () => LoadTranscriptsAsync(userId, cancellationToken),
            cancellationToken);

        return new AssistantContextOptions(quizzes, books, transcripts);
    }

    private async Task<IReadOnlyList<AssistantMaterialOption>> LoadTranscriptsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var selectedLanguage = await _languagePreferences.GetSelectedAsync(userId, cancellationToken);
        if (selectedLanguage is null || !selectedLanguage.IsLanguageLearning)
        {
            return [];
        }

        var library = await _transcripts.GetLibraryAsync(
            userId,
            selectedLanguage.Code,
            page: 1,
            pageSize: TranscriptPageSize,
            cancellationToken);

        return library.Items
            .Select(item => new AssistantMaterialOption(item.Id, item.Title))
            .ToArray();
    }

    private async Task<IReadOnlyList<T>> TryLoadAsync<T>(
        string source,
        Func<Task<IReadOnlyList<T>>> load,
        CancellationToken cancellationToken)
    {
        try
        {
            return await load();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not load assistant context options from {Source}",
                source);
            return [];
        }
    }
}
