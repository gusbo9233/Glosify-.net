using System.Globalization;
using Glosify.Extensions;
using Glosify.Models.ViewModels;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.ViewComponents;

/// <summary>
/// Loads the assistant panel's context pickers.
/// </summary>
/// <remarks>
/// This work used to live in the panel partial, which meant five services injected into a
/// view and three database round-trips on every authenticated page render, with no error
/// path — a failed query there took down whatever page the user had asked for. As a view
/// component it has its own dependencies, is directly testable, and degrades to empty
/// pickers when the lookups fail, because a missing dropdown is not a reason to serve a 500.
/// </remarks>
public sealed class AssistantPanelViewComponent : ViewComponent
{
    private const int TranscriptPageSize = 50;

    private readonly IGenerativeAiModelResolver _models;
    private readonly ICreditPricingResolver _pricing;
    private readonly IQuizService _quizzes;
    private readonly IBookDocumentService _books;
    private readonly IRealtimeTranslationTranscriptService _transcripts;
    private readonly IQuizLanguagePreferenceService _languagePreferences;
    private readonly ILogger<AssistantPanelViewComponent> _logger;

    public AssistantPanelViewComponent(
        IGenerativeAiModelResolver models,
        ICreditPricingResolver pricing,
        IQuizService quizzes,
        IBookDocumentService books,
        IRealtimeTranslationTranscriptService transcripts,
        IQuizLanguagePreferenceService languagePreferences,
        ILogger<AssistantPanelViewComponent> logger)
    {
        _models = models;
        _pricing = pricing;
        _quizzes = quizzes;
        _books = books;
        _transcripts = transcripts;
        _languagePreferences = languagePreferences;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync(AssistantPanelViewModel panel)
    {
        var models = ModelOptions();
        var defaultModelLabel = models.FirstOrDefault(model => string.Equals(
            model.Deployment,
            _models.DefaultAssistantModel,
            StringComparison.OrdinalIgnoreCase))?.Label ?? "default model";

        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true)
        {
            return View(new AssistantPanelContentViewModel
            {
                Panel = panel,
                DefaultModelLabel = defaultModelLabel,
                Models = models,
            });
        }

        var userId = UserClaimsPrincipal.GetUserId();
        var cancellationToken = HttpContext.RequestAborted;

        IReadOnlyList<AssistantQuizOption> quizzes = [];
        IReadOnlyList<AssistantMaterialOption> books = [];
        IReadOnlyList<AssistantMaterialOption> transcripts = [];

        try
        {
            quizzes = await LoadQuizzesAsync(userId, cancellationToken);
            books = await LoadBooksAsync(userId, cancellationToken);
            transcripts = await LoadTranscriptsAsync(userId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever loaded before the failure is still worth showing, so the partial
            // results are kept rather than reset.
            _logger.LogWarning(
                exception,
                "Could not load the assistant panel's context pickers; rendering without them");
        }

        return View(new AssistantPanelContentViewModel
        {
            Panel = panel,
            DefaultModelLabel = defaultModelLabel,
            Models = models,
            Quizzes = quizzes,
            Books = books,
            Transcripts = transcripts,
        });
    }

    private async Task<IReadOnlyList<AssistantQuizOption>> LoadQuizzesAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var quizzes = await _quizzes.GetUserQuizzesAsync(userId, cancellationToken);
        return quizzes
            .Select(quiz => new AssistantQuizOption(
                quiz.Id,
                quiz.Name,
                quiz.SourceLanguage,
                quiz.TargetLanguage))
            .ToArray();
    }

    private async Task<IReadOnlyList<AssistantMaterialOption>> LoadBooksAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var books = await _books.GetUserBooksAsync(userId, cancellationToken);
        return books
            .Select(book => new AssistantMaterialOption(book.Id, book.Title))
            .ToArray();
    }

    /// <summary>
    /// Transcripts are keyed by language code while books use the display name, so the two
    /// lists are fetched separately rather than through one shared filter.
    /// </summary>
    private async Task<IReadOnlyList<AssistantMaterialOption>> LoadTranscriptsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var selectedLanguage = await _languagePreferences.GetSelectedAsync(userId, cancellationToken);
        if (selectedLanguage is null)
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

    private IReadOnlyList<AssistantModelOption> ModelOptions() =>
        _models.AssistantModels
            .Select(model => new AssistantModelOption(
                model.Deployment,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{model.DisplayName} · {model.Provider} · {model.SpeedTier} · {_pricing.GetModelMultiplier(model.Deployment):0.##}× credits")))
            .ToArray();
}
