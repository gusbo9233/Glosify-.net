using Glosify.Models.Api;
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
    public async Task<ActionResult<IReadOnlyList<CollectionDto>>> List([FromQuery] string language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return BadRequest("language is required.");
        }

        var collections = await _collectionService.GetCollectionsAsync(User.GetUserId(), language, cancellationToken: cancellationToken);
        return Ok(collections.Select(CollectionDto.From).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CollectionDto>> Create([FromBody] CreateCollectionRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Language))
        {
            return BadRequest("Name and Language are required.");
        }

        try
        {
            var collection = await _collectionService.CreateCollectionAsync(
                request.Name.Trim(), request.Language.Trim(), User.GetUserId(), request.ParentCollectionId, cancellationToken: cancellationToken);

            return Ok(CollectionDto.From(collection));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Unknown or foreign parent collection id.
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id:guid}/name")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameCollectionRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        var renamed = await _collectionService.RenameCollectionAsync(id, request.Name.Trim(), User.GetUserId(), cancellationToken: cancellationToken);
        return renamed ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await _collectionService.DeleteCollectionAsync(id, User.GetUserId(), cancellationToken: cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPut("{id:guid}/visibility")]
    public async Task<IActionResult> SetVisibility(Guid id, [FromBody] SetVisibilityRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _collectionService.SetCollectionPublicAsync(id, User.GetUserId(), request.IsPublic, cancellationToken: cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpPut("quizzes/{quizId:guid}")]
    public async Task<IActionResult> MoveQuiz(Guid quizId, [FromBody] MoveQuizRequest request, CancellationToken cancellationToken = default)
    {
        var moved = await _collectionService.MoveQuizToCollectionAsync(quizId, request.CollectionId, User.GetUserId(), cancellationToken: cancellationToken);
        return moved ? NoContent() : NotFound();
    }
}
