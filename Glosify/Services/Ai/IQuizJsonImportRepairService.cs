using Glosify.Models.QuizImports;

namespace Glosify.Services.Ai;

public interface IQuizJsonImportRepairService
{
    Task<QuizJsonImportPreview> RepairAsync(
        string json,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        CancellationToken cancellationToken = default);
}
