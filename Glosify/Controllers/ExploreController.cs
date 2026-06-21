using Glosify.Models;
using Glosify.Services;
using Glosify.Services.Quizzes;
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
    public async Task<IActionResult> Index()
    {
        var language = _languageContext.CurrentLanguage;
        if (language == null)
        {
            return RedirectToAction("Index", "Languages");
        }

        var collections = await _collectionService.GetPublicCollectionRootsAsync(language);
        var quizzes = await _quizService.GetPublicQuizzesAsync(language);
        var collectionCards = new List<ExploreCollectionCardViewModel>();
        foreach (var collection in collections)
        {
            var tree = await _collectionService.GetPublicCollectionTreeAsync(collection.Id);
            collectionCards.Add(new ExploreCollectionCardViewModel
            {
                Collection = collection,
                CollectionCount = tree == null ? 0 : CountDescendantCollections(tree),
                QuizCount = tree == null ? 0 : CountQuizzes(tree)
            });
        }

        var quizCards = new List<ExploreQuizCardViewModel>();
        foreach (var quiz in quizzes)
        {
            quizCards.Add(new ExploreQuizCardViewModel
            {
                Quiz = quiz,
                WordCount = await _quizService.GetAvailableWordCountAsync(quiz.Id)
            });
        }

        return View(new ExploreIndexViewModel
        {
            CurrentLanguage = language,
            Collections = collectionCards,
            Quizzes = quizCards
        });
    }

    [HttpGet]
    public async Task<IActionResult> Collection(Guid id)
    {
        var collection = await _collectionService.GetPublicCollectionTreeAsync(id);
        if (collection == null)
        {
            return RedirectToAction(nameof(Index));
        }

        return View(new ExploreCollectionViewModel
        {
            Collection = collection,
            CollectionCount = CountDescendantCollections(collection),
            QuizCount = CountQuizzes(collection)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var selectedQuiz = await _quizService.GetPublicQuizAsync(id);
        if (selectedQuiz == null)
        {
            TempData["ExploreMessage"] = "That quiz is no longer public.";
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

        var words = await _wordService.GetWordsAsync(selectedQuiz.Id);
        var sentences = await _wordService.GetSentencesAsync(selectedQuiz.Id);

        if (selectedQuiz.CollectionId is Guid collectionId
            && await _collectionService.GetPublicCollectionTreeAsync(collectionId) != null)
        {
            ViewData["BackCollectionId"] = collectionId;
        }

        return View(new QuizWorkspaceViewModel
        {
            SelectedQuiz = selectedQuiz,
            Words = words,
            Sentences = sentences.Select(s => new QuizSentenceViewModel
            {
                Text = s.Text,
                Translation = s.Translation,
                WordCount = s.WordCount
            }).ToList()
        });
    }

    [HttpPost]
    public async Task<IActionResult> CopyQuiz(Guid id)
    {
        var copied = await _quizService.CopyPublicQuizAsync(id, User.GetUserId());
        if (copied == null)
        {
            TempData["ExploreMessage"] = "That quiz is no longer public.";
            return RedirectToAction(nameof(Index));
        }

        TempData["QuizMessage"] = $"Copied {copied.Name} to your library.";
        return RedirectToAction("Details", "Quiz", new { id = copied.Id });
    }

    [HttpPost]
    public async Task<IActionResult> CopyCollection(Guid id)
    {
        var copied = await _collectionService.CopyPublicCollectionAsync(id, User.GetUserId());
        if (copied == null)
        {
            TempData["ExploreMessage"] = "That collection is no longer public.";
            return RedirectToAction(nameof(Index));
        }

        TempData["QuizMessage"] = $"Copied {copied.Name} to your library.";
        return RedirectToAction("Collection", "Quiz", new { id = copied.Id });
    }

    private static int CountDescendantCollections(Collection collection)
    {
        return collection.ChildCollections.Count
            + collection.ChildCollections.Sum(CountDescendantCollections);
    }

    private static int CountQuizzes(Collection collection)
    {
        return collection.Quizzes.Count
            + collection.ChildCollections.Sum(CountQuizzes);
    }
}
