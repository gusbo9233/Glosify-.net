using Glosify.Services;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/collections")]
public class CollectionsApiController : ApiControllerBase
{
    private readonly ICollectionService _collectionService;

    public CollectionsApiController(ICollectionService collectionService)
    {
        _collectionService = collectionService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CollectionDto>>> List([FromQuery] string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return BadRequest("language is required.");
        }

        var collections = await _collectionService.GetCollectionsAsync(User.GetUserId(), language);
        return Ok(collections.Select(CollectionDto.From).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CollectionDto>> Create([FromBody] CreateCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Language))
        {
            return BadRequest("Name and Language are required.");
        }

        var collection = await _collectionService.CreateCollectionAsync(
            request.Name.Trim(), request.Language.Trim(), User.GetUserId(), request.ParentCollectionId);

        return Ok(CollectionDto.From(collection));
    }

    [HttpPut("{id:guid}/name")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        var renamed = await _collectionService.RenameCollectionAsync(id, request.Name.Trim(), User.GetUserId());
        return renamed ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await _collectionService.DeleteCollectionAsync(id, User.GetUserId());
        return deleted ? NoContent() : NotFound();
    }

    [HttpPut("quizzes/{quizId:guid}")]
    public async Task<IActionResult> MoveQuiz(Guid quizId, [FromBody] MoveQuizRequest request)
    {
        var moved = await _collectionService.MoveQuizToCollectionAsync(quizId, request.CollectionId, User.GetUserId());
        return moved ? NoContent() : NotFound();
    }
}

public sealed record CollectionDto(Guid Id, string Name, string Language, Guid? ParentCollectionId)
{
    public static CollectionDto From(Collection collection) =>
        new(collection.Id, collection.Name, collection.Language, collection.ParentCollectionId);
}

public sealed record CreateCollectionRequest(string Name, string Language, Guid? ParentCollectionId);

public sealed record RenameCollectionRequest(string Name);

public sealed record MoveQuizRequest(Guid? CollectionId);
