using Glosify.Models.Api;
using Glosify.Filters;
using Glosify.Services;
using Microsoft.AspNetCore.Mvc;
using Glosify.Services.Ai;
using Glosify.Services.Quizzes;
using Glosify.Services.Typing;
using Glosify.Services.Words;

namespace Glosify.Controllers.Api;

[Route("api/quizzes")]
public class QuizzesApiController : ApiControllerBase
{
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly ITypingQuizService _typingQuizService;
    private readonly IQuizRepairService _quizRepairService;
    private readonly IImageTextExtractionService _imageTextExtractionService;

    public QuizzesApiController(
        IQuizService quizService,
        IWordService wordService,
        ITypingQuizService typingQuizService,
        IQuizRepairService quizRepairService,
        IImageTextExtractionService imageTextExtractionService)
    {
        _quizService = quizService;
        _wordService = wordService;
        _typingQuizService = typingQuizService;
        _quizRepairService = quizRepairService;
        _imageTextExtractionService = imageTextExtractionService;
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
        var sentenceCount = await _quizService.GetAvailableSentenceCountAsync(id);
        return Ok(QuizDetailDto.From(quiz, wordCount, sentenceCount));
    }

    [HttpPut("{id:guid}/visibility")]
    public async Task<IActionResult> SetVisibility(Guid id, [FromBody] SetVisibilityRequest request)
    {
        var updated = await _quizService.SetQuizPublicAsync(id, User.GetUserId(), request.IsPublic);
        return updated ? NoContent() : NotFound();
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

        try
        {
            var quiz = await _quizService.CreateQuizAsync(
                request.Name.Trim(),
                request.SourceLanguage.Trim(),
                request.TargetLanguage.Trim(),
                User.GetUserId(),
                request.CollectionId);

            return CreatedAtAction(nameof(Get), new { id = quiz.Id }, QuizSummaryDto.From(quiz));
        }
        catch (InvalidOperationException ex)
        {
            // Unknown or foreign collection id.
            return BadRequest(ex.Message);
        }
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
        return Ok(words.Select(w => new WordDto(w.Id, w.Lemma, w.Translation, w.CreatedAt)).ToList());
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
    public async Task<ActionResult<IReadOnlyList<SentenceDto>>> Sentences(Guid id)
    {
        if (!await _quizService.UserOwnsQuizAsync(id, User.GetUserId()))
        {
            return NotFound();
        }

        var sentences = await _wordService.GetSentencesAsync(id);
        return Ok(sentences.Select(s => new SentenceDto(s.Id, s.Text, s.Translation, s.WordCount)).ToList());
    }

    [HttpPost("words/{wordId}/repair")]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> RepairWord(string wordId, CancellationToken cancellationToken)
    {
        var result = await _quizRepairService.RepairWordAsync(wordId, User.GetUserId(), cancellationToken);
        return result.Status switch
        {
            QuizRepairStatus.NotFound => NotFound("Word not found."),
            QuizRepairStatus.LlmUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, ServiceWarmupMessage.LlmAssistant),
            _ => Ok(new RepairResultDto($"Repaired {result.Word}."))
        };
    }

    [HttpPost("{id:guid}/sentences/repair")]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> RepairSentence(Guid id, [FromBody] RepairSentenceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return BadRequest("Choose a sentence to repair.");
        }

        var result = await _quizRepairService.RepairSentenceAsync(id, request.Text, User.GetUserId(), cancellationToken);
        return result.Status switch
        {
            QuizRepairStatus.NotFound => NotFound("Quiz not found."),
            QuizRepairStatus.LlmUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, ServiceWarmupMessage.LlmAssistant),
            _ => Ok(new RepairResultDto(result.UpdatedCount == 1
                ? "Sentence repaired."
                : $"Sentence repaired in {result.UpdatedCount} places."))
        };
    }

    [HttpPost("{id:guid}/extract-image-text")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> ExtractTextFromImage(Guid id, IFormFile? image, CancellationToken cancellationToken)
    {
        var quiz = await _quizService.GetQuizByIdAsync(id, User.GetUserId());
        if (quiz == null)
        {
            return NotFound("Quiz not found.");
        }

        if (image == null || image.Length == 0)
        {
            return BadRequest("Take or choose a photo first.");
        }

        if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Choose an image file.");
        }

        await using var stream = image.OpenReadStream();
        var text = await _imageTextExtractionService.ExtractTextAsync(
            User.GetUserId(), stream, image.ContentType,
            quiz.SourceLanguage, quiz.TargetLanguage, cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return UnprocessableEntity("No readable text was found in that image.");
        }

        return Ok(new ExtractedTextDto(text));
    }
}
