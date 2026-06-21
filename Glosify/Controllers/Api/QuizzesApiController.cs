using Glosify.Services;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/quizzes")]
public class QuizzesApiController : ApiControllerBase
{
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly ITypingQuizService _typingQuizService;

    public QuizzesApiController(
        IQuizService quizService,
        IWordService wordService,
        ITypingQuizService typingQuizService)
    {
        _quizService = quizService;
        _wordService = wordService;
        _typingQuizService = typingQuizService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<QuizSummaryDto>>> List([FromQuery] string? language)
    {
        var quizzes = await _quizService.GetUserQuizzesAsync(User.GetUserId());
        var filtered = string.IsNullOrWhiteSpace(language)
            ? quizzes
            : quizzes.Where(q =>
                string.Equals(q.Language, language, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(q.TargetLanguage, language, StringComparison.OrdinalIgnoreCase)).ToList();

        return Ok(filtered.Select(QuizSummaryDto.From).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuizDetailDto>> Get(Guid id)
    {
        var quiz = await _quizService.GetQuizByIdAsync(id, User.GetUserId());
        if (quiz == null)
        {
            return NotFound();
        }

        var wordCount = await _quizService.GetAvailableWordCountAsync(id);
        return Ok(QuizDetailDto.From(quiz, wordCount));
    }

    [HttpPost]
    public async Task<ActionResult<QuizSummaryDto>> Create([FromBody] CreateQuizRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.SourceLanguage) ||
            string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            return BadRequest("Name, SourceLanguage and TargetLanguage are required.");
        }

        var quiz = await _quizService.CreateQuizAsync(
            request.Name.Trim(),
            request.SourceLanguage.Trim(),
            request.TargetLanguage.Trim(),
            User.GetUserId(),
            request.CollectionId);

        return CreatedAtAction(nameof(Get), new { id = quiz.Id }, QuizSummaryDto.From(quiz));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await _quizService.DeleteQuizAsync(id, User.GetUserId());
        return deleted == null ? NotFound() : NoContent();
    }

    [HttpGet("{id:guid}/words")]
    public async Task<ActionResult<IReadOnlyList<WordDto>>> Words(Guid id)
    {
        if (!await _quizService.UserOwnsQuizAsync(id, User.GetUserId()))
        {
            return NotFound();
        }

        var words = await _wordService.GetWordsAsync(id);
        return Ok(words.Select(w => new WordDto(w.Id, w.Lemma, w.Translation)).ToList());
    }

    [HttpPost("{id:guid}/words")]
    public async Task<IActionResult> AddWord(Guid id, [FromBody] AddWordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Word) || string.IsNullOrWhiteSpace(request.Translation))
        {
            return BadRequest("Word and Translation are required.");
        }

        var quiz = await _quizService.GetQuizByIdAsync(id, User.GetUserId());
        if (quiz == null)
        {
            return NotFound();
        }

        if (await _wordService.WordExistsAsync(id, request.Word.Trim()))
        {
            return Conflict("The word already exists in this quiz.");
        }

        var added = await _wordService.AddWordAsync(
            id, request.Word.Trim(), request.Translation.Trim(), quiz.SourceLanguage, quiz.TargetLanguage);

        return added ? NoContent() : BadRequest("Could not add the word.");
    }

    [HttpDelete("words/{wordId}")]
    public async Task<IActionResult> DeleteWord(string wordId)
    {
        var deleted = await _wordService.DeleteWordAsync(wordId, User.GetUserId());
        return deleted == null ? NotFound() : NoContent();
    }

    [HttpGet("{id:guid}/cards")]
    public async Task<ActionResult<IReadOnlyList<QuizCardData>>> Cards(Guid id, [FromQuery] int count = 20, [FromQuery] string? practiceDirection = null, [FromQuery] string? practiceItemType = null)
    {
        if (!await _quizService.UserOwnsQuizAsync(id, User.GetUserId()))
        {
            return NotFound();
        }

        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);
        var cards = PracticeItemType.IsSentences(normalizedItemType)
            ? await _wordService.LoadSentenceCardsAsync(id, Math.Clamp(count, 1, 100))
            : await _wordService.LoadCardsAsync(id, Math.Clamp(count, 1, 100));
        return Ok(cards.Select(card => new QuizCardData
        {
            Id = card.Id,
            Lemma = card.Lemma,
            Translation = card.Translation,
            Prompt = PracticeDirection.IsSourceToTarget(normalizedDirection) ? card.Translation : card.Lemma,
            Answer = PracticeDirection.IsSourceToTarget(normalizedDirection) ? card.Lemma : card.Translation,
            ExampleSentence = card.ExampleSentence,
            ExampleTranslation = card.ExampleTranslation
        }).ToList());
    }

    [HttpGet("{id:guid}/typing")]
    public async Task<ActionResult<TypingQuizData>> Typing(Guid id, [FromQuery] int count = 20, [FromQuery] string? practiceDirection = null, [FromQuery] string? practiceItemType = null)
    {
        if (!await _quizService.UserOwnsQuizAsync(id, User.GetUserId()))
        {
            return NotFound();
        }

        return Ok(await _typingQuizService.GetQuizDataAsync(id, Math.Clamp(count, 1, 100), practiceDirection, practiceItemType));
    }

    [HttpGet("{id:guid}/sentences")]
    public async Task<ActionResult<IReadOnlyList<QuizSentenceData>>> Sentences(Guid id)
    {
        if (!await _quizService.UserOwnsQuizAsync(id, User.GetUserId()))
        {
            return NotFound();
        }

        return Ok(await _wordService.GetSentencesAsync(id));
    }
}

public sealed record QuizSummaryDto(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string Language,
    Guid? CollectionId,
    DateTimeOffset CreatedAt)
{
    public static QuizSummaryDto From(Quiz quiz) => new(
        quiz.Id, quiz.Name, quiz.SourceLanguage, quiz.TargetLanguage,
        quiz.Language, quiz.CollectionId, quiz.CreatedAt);
}

public sealed record QuizDetailDto(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string Language,
    Guid? CollectionId,
    DateTimeOffset CreatedAt,
    int WordCount)
{
    public static QuizDetailDto From(Quiz quiz, int wordCount) => new(
        quiz.Id, quiz.Name, quiz.SourceLanguage, quiz.TargetLanguage,
        quiz.Language, quiz.CollectionId, quiz.CreatedAt, wordCount);
}

public sealed record WordDto(string Id, string Lemma, string Translation);

public sealed record CreateQuizRequest(string Name, string SourceLanguage, string TargetLanguage, Guid? CollectionId);

public sealed record AddWordRequest(string Word, string Translation);
