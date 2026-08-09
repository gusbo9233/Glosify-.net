using System.ComponentModel.DataAnnotations;
using Glosify.Models.Entities;
namespace Glosify.Models.Api;

public sealed record CollectionDto(Guid Id, string Name, string Language, Guid? ParentCollectionId, bool IsPublic)
{
    public static CollectionDto From(Collection collection) =>
        new(collection.Id, collection.Name, collection.Language, collection.ParentCollectionId, collection.IsPublic);
}

public sealed record CreateCollectionRequest(
    [param: Required, StringLength(160)] string Name,
    [param: Required, StringLength(64)] string Language,
    Guid? ParentCollectionId);

public sealed record RenameCollectionRequest(
    [param: Required, StringLength(160)] string Name);

public sealed record MoveQuizRequest(Guid? CollectionId);
