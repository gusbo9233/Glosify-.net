using Glosify.Models.CustomQuizzes;

namespace Glosify.Services.CustomQuizzes;

public interface ICustomQuizService
{
    Task<IReadOnlyList<CustomQuizSummaryDto>> ListForQuizAsync(Guid quizId, bool playableOnly = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomQuizSummaryDto>>> ListForQuizzesAsync(
        IReadOnlyCollection<Guid> quizIds,
        bool playableOnly = false,
        CancellationToken cancellationToken = default);
    Task<CustomQuizEditorDto?> GetForEditorAsync(Guid id, string userId, CancellationToken cancellationToken = default);
    Task<CustomQuizEditorDto> CreateAsync(SaveCustomQuizRequest request, string userId, CancellationToken cancellationToken = default);
    Task<CustomQuizEditorDto?> UpdateAsync(Guid id, SaveCustomQuizRequest request, string userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default);
    Task<CustomQuizPlayData?> GetForPlayAsync(Guid id, string userId, Guid? classroomId = null, CancellationToken cancellationToken = default);
    Task<CustomQuizGradeResult?> GradeAsync(Guid id, GradeCustomQuizRequest request, string userId, CancellationToken cancellationToken = default);
    Task PruneWordBindingsAsync(Guid quizId, IReadOnlyCollection<string> wordIds, CancellationToken cancellationToken = default);
    Task CloneForCopiedQuizAsync(Guid sourceQuizId, Guid targetQuizId, IReadOnlyDictionary<string, string> wordIdMap, CancellationToken cancellationToken = default);
    CustomQuizDocumentV1 RemapWordBindings(CustomQuizDocumentV1 document, IReadOnlyDictionary<string, string> wordIdMap);
    CustomQuizValidationResult Validate(CustomQuizDocumentV1 document, IReadOnlyDictionary<string, Word> words);
}

public sealed record CustomQuizPlayData(
    Guid Id,
    Guid QuizId,
    string Name,
    string SourceQuizName,
    string SourceLanguage,
    string TargetLanguage,
    Guid AttemptId,
    CustomQuizDocumentV1 Document,
    IReadOnlyDictionary<string, string> ResolvedValues,
    Guid? ClassroomId);
