using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public sealed class CreateCollectionInput
{
    [Required(ErrorMessage = "Collection name is required.")]
    [StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    public Guid? ParentCollectionId { get; set; }
}
