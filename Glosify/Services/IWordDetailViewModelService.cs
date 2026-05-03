using Glosify.Models;

namespace Glosify.Services;

public interface IWordDetailViewModelService
{
    // Returns null if the detail does not exist or does not belong to the user.
    Task<WordDetailViewModel?> BuildAsync(string wordDetailId, string userId);
}
