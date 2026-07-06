using Glosify.Models.Api;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Mvc;
using Glosify.Services.Words;

namespace Glosify.Controllers.Api;

/// <summary>
/// Public catalogue (Explore) endpoints for the mobile app, mirroring ExploreController.
/// </summary>
[Route("api/explore")]
public class ExploreApiController : ApiControllerBase
{
    private readonly ICollectionService _collectionService;
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;

    public ExploreApiController(
        ICollectionService collectionService,
        IQuizService quizService,
        IWordService wordService)
    {
        _collectionService = collectionService;
        _quizService = quizService;
        _wordService = wordService;
    }

    [HttpGet]
    public async Task<ActionResult<ExploreIndexDto>> Index([FromQuery] string language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return BadRequest("language is required.");
        }

        var summaries = await _collectionService.GetPublicCollectionSummariesAsync(language, cancellationToken: cancellationToken);
        var collectionCards = summaries
            .Select(summary => new ExploreCollectionCardDto(
                summary.Collection.Id,
                summary.Collection.Name,
                summary.Collection.Language,
                summary.CollectionCount,
                summary.QuizCount))
            .ToList();

        var quizzes = await _quizService.GetPublicQuizzesAsync(language, cancellationToken: cancellationToken);
        var wordCounts = await _quizService.GetWordCountsAsync(quizzes.Select(quiz => quiz.Id).ToList(), cancellationToken: cancellationToken);
        var quizCards = quizzes
            .Select(quiz => new ExploreQuizCardDto(
                quiz.Id, quiz.Name, quiz.SourceLanguage, quiz.TargetLanguage,
                wordCounts.GetValueOrDefault(quiz.Id)))
            .ToList();

        return Ok(new ExploreIndexDto(collectionCards, quizCards));
    }

    [HttpGet("collections/{id:guid}")]
    public async Task<ActionResult<ExploreCollectionDto>> Collection(Guid id, CancellationToken cancellationToken = default)
    {
        var collection = await _collectionService.GetPublicCollectionTreeAsync(id, cancellationToken: cancellationToken);
        if (collection == null)
        {
            return NotFound();
        }

        var quizIds = CollectQuizIds(collection);
        var wordCounts = await _quizService.GetWordCountsAsync(quizIds, cancellationToken: cancellationToken);
        return Ok(ToDto(collection, wordCounts));
    }

    [HttpGet("quizzes/{id:guid}")]
    public async Task<ActionResult<ExploreQuizDetailDto>> Quiz(Guid id, CancellationToken cancellationToken = default)
    {
        var quiz = await _quizService.GetPublicQuizAsync(id, cancellationToken: cancellationToken);
        if (quiz == null)
        {
            return NotFound();
        }

        var words = await _wordService.GetWordsAsync(quiz.Id, cancellationToken: cancellationToken);
        var sentences = await _wordService.GetSentencesAsync(quiz.Id, cancellationToken: cancellationToken);

        return Ok(new ExploreQuizDetailDto(
            quiz.Id,
            quiz.Name,
            quiz.SourceLanguage,
            quiz.TargetLanguage,
            words.Select(w => new WordDto(w.Id, w.Lemma, w.Translation, w.CreatedAt)).ToList(),
            sentences.Select(s => new SentenceDto(s.Id, s.Text, s.Translation, s.WordCount)).ToList()));
    }

    [HttpPost("quizzes/{id:guid}/copy")]
    public async Task<ActionResult<QuizSummaryDto>> CopyQuiz(Guid id, CancellationToken cancellationToken = default)
    {
        var copied = await _quizService.CopyPublicQuizAsync(id, User.GetUserId(), cancellationToken: cancellationToken);
        if (copied == null)
        {
            return NotFound("That quiz is no longer public.");
        }

        return Ok(QuizSummaryDto.From(copied));
    }

    [HttpPost("collections/{id:guid}/copy")]
    public async Task<ActionResult<CollectionDto>> CopyCollection(Guid id, CancellationToken cancellationToken = default)
    {
        var copied = await _collectionService.CopyPublicCollectionAsync(id, User.GetUserId(), cancellationToken: cancellationToken);
        if (copied == null)
        {
            return NotFound("That collection is no longer public.");
        }

        return Ok(CollectionDto.From(copied));
    }

    private static ExploreCollectionDto ToDto(Collection collection, IReadOnlyDictionary<Guid, int> wordCounts) =>
        new(
            collection.Id,
            collection.Name,
            collection.Language,
            collection.Quizzes
                .Select(quiz => new ExploreQuizCardDto(
                    quiz.Id, quiz.Name, quiz.SourceLanguage, quiz.TargetLanguage,
                    wordCounts.GetValueOrDefault(quiz.Id)))
                .ToList(),
            collection.ChildCollections.Select(child => ToDto(child, wordCounts)).ToList());

    private static List<Guid> CollectQuizIds(Collection collection)
    {
        var ids = collection.Quizzes.Select(quiz => quiz.Id).ToList();
        foreach (var child in collection.ChildCollections)
        {
            ids.AddRange(CollectQuizIds(child));
        }
        return ids;
    }

}
