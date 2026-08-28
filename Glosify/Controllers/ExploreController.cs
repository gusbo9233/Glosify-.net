using Glosify.Extensions;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Glosify.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class ExploreController : Controller
{
    private readonly ICollectionService _collectionService;
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly ILanguageContext _languageContext;
    private readonly UiTextStringLocalizer _text = new();

    public ExploreController(
        ICollectionService collectionService,
        IQuizService quizService,
        IWordService wordService,
        ILanguageContext languageContext)
    {
        _collectionService = collectionService;
        _quizService = quizService;
        _wordService = wordService;
        _languageContext = languageContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var language = _languageContext.CurrentLanguage;
        if (language == null)
        {
            return RedirectToAction("Index", "Languages");
        }

        var summaries = await _collectionService.GetPublicCollectionSummariesAsync(language, cancellationToken: cancellationToken);
        var quizzes = await _quizService.GetPublicQuizzesAsync(language, cancellationToken: cancellationToken);
        var collectionCards = summaries
            .Select(summary => new ExploreCollectionCardViewModel
            {
                Collection = CollectionCard.From(summary.Collection),
                CollectionCount = summary.CollectionCount,
                QuizCount = summary.QuizCount
            })
            .ToList();

        var wordCounts = await _quizService.GetWordCountsAsync(quizzes.Select(quiz => quiz.Id).ToList(), cancellationToken: cancellationToken);
        var quizCards = quizzes
            .Select(quiz => new ExploreQuizCardViewModel
            {
                Quiz = QuizCard.From(quiz),
                WordCount = wordCounts.GetValueOrDefault(quiz.Id)
            })
            .ToList();

        return View(new ExploreIndexViewModel
        {
            CurrentLanguage = language,
            Collections = collectionCards,
            Quizzes = quizCards
        });
    }

    [HttpGet]
    public async Task<IActionResult> Collection(Guid id, CancellationToken cancellationToken = default)
    {
        var collection = await _collectionService.GetPublicCollectionTreeAsync(id, cancellationToken: cancellationToken);
        if (collection == null)
        {
            return RedirectToAction(nameof(Index));
        }

        var tree = ExploreCollectionNode.From(collection);

        return View(new ExploreCollectionViewModel
        {
            Collection = tree,
            CollectionCount = tree.DescendantCollectionCount,
            QuizCount = tree.TotalQuizCount
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken = default)
    {
        var selectedQuiz = await _quizService.GetPublicQuizAsync(id, cancellationToken: cancellationToken);
        if (selectedQuiz == null)
        {
            TempData[NotificationKeys.Explore] = _text["Explore.QuizNoLongerPublic"].Value;
            return RedirectToAction(nameof(Index));
        }

        var language = _languageContext.CurrentLanguage;
        if (language == null)
        {
            return RedirectToAction("Index", "Languages");
        }

        if (!string.Equals(selectedQuiz.TargetLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Index));
        }

        var words = await _wordService.GetWordsAsync(selectedQuiz.Id, cancellationToken: cancellationToken);
        var sentences = await _wordService.GetSentencesAsync(selectedQuiz.Id, cancellationToken: cancellationToken);

        if (selectedQuiz.CollectionId is Guid collectionId
            && await _collectionService.GetPublicCollectionTreeAsync(collectionId, cancellationToken: cancellationToken) != null)
        {
            ViewData["BackCollectionId"] = collectionId;
        }

        return View(new QuizWorkspaceViewModel
        {
            SelectedQuiz = QuizCard.From(selectedQuiz),
            Words = words.Select(WordRow.From).ToList(),
            Sentences = sentences.Select(s => new QuizSentenceViewModel
            {
                Text = s.Text,
                Translation = s.Translation,
                WordCount = s.WordCount
            }).ToList()
        });
    }

    [HttpPost]
    public async Task<IActionResult> CopyQuiz(Guid id, CancellationToken cancellationToken = default)
    {
        var copied = await _quizService.CopyPublicQuizAsync(id, User.GetUserId(), cancellationToken: cancellationToken);
        if (copied == null)
        {
            TempData[NotificationKeys.Explore] = _text["Explore.QuizNoLongerPublic"].Value;
            return RedirectToAction(nameof(Index));
        }

        TempData[NotificationKeys.Quiz] = _text["Explore.CopiedToLibrary", copied.Name].Value;
        return RedirectToAction("Details", "Quiz", new { id = copied.Id });
    }

    [HttpPost]
    public async Task<IActionResult> CopyCollection(Guid id, CancellationToken cancellationToken = default)
    {
        var copied = await _collectionService.CopyPublicCollectionAsync(id, User.GetUserId(), cancellationToken: cancellationToken);
        if (copied == null)
        {
            TempData[NotificationKeys.Explore] = _text["Explore.CollectionNoLongerPublic"].Value;
            return RedirectToAction(nameof(Index));
        }

        TempData[NotificationKeys.Quiz] = _text["Explore.CopiedToLibrary", copied.Name].Value;
        return RedirectToAction("Collection", "Quiz", new { id = copied.Id });
    }
}
