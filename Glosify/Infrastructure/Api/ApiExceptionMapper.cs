using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Books;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speaking;

namespace Glosify.Infrastructure.Api;

public readonly record struct ApiError(int StatusCode, string Code, string Detail);

public static class ApiExceptionMapper
{
    public static ApiError? Map(Exception exception) => exception switch
    {
        CollectionParentNotFoundException => Error(400, ApiErrorCodes.CollectionParentNotFound, exception),
        CollectionNameConflictException => Error(409, ApiErrorCodes.CollectionNameConflict, exception),
        QuizCollectionNotFoundException => Error(400, ApiErrorCodes.QuizCollectionNotFound, exception),
        QuizJsonImportValidationException => Error(400, ApiErrorCodes.ValidationFailed, exception),
        QuizJsonImportAiUnprocessableException => Error(422, ApiErrorCodes.UnprocessableEntity, exception),
        AssistantTurnInProgressException => Error(409, ApiErrorCodes.Conflict, exception),
        AssistantFeedbackValidationException => Error(400, ApiErrorCodes.BadRequest, exception),
        AssistantTurnNotFoundException => new ApiError(
            404,
            ApiErrorCodes.NotFound,
            "Assistant turn not found."),
        InsufficientAiCreditsException => Error(402, ApiErrorCodes.PaymentRequired, exception),
        PaidServicesBudgetExhaustedException => Error(503, ApiErrorCodes.PaidServicesBudgetExhausted, exception),
        GenerativeAiValidationException => Error(400, ApiErrorCodes.ValidationFailed, exception),
        GenerativeAiDependencyUnavailableException => Error(503, ApiErrorCodes.DependencyUnavailable, exception),
        GenerativeAiTimeoutException => Error(504, ApiErrorCodes.Timeout, exception),
        GenerativeAiUpstreamException => Error(502, ApiErrorCodes.UpstreamFailure, exception),
        BookPageTranslationValidationException => Error(400, ApiErrorCodes.ValidationFailed, exception),
        BookPageTranslationNotFoundException => Error(404, ApiErrorCodes.NotFound, exception),
        BookPageTranslationUnavailableException => Error(422, ApiErrorCodes.UnprocessableEntity, exception),
        RealtimeTranslationValidationException => Error(400, ApiErrorCodes.ValidationFailed, exception),
        RealtimeTranslationNotFoundException => Error(404, ApiErrorCodes.NotFound, exception),
        RealtimeTranslationConflictException => Error(409, ApiErrorCodes.Conflict, exception),
        RealtimeTranslationExpiredException => Error(410, ApiErrorCodes.Gone, exception),
        RealtimeTranslationUnavailableException => Error(503, ApiErrorCodes.DependencyUnavailable, exception),
        RealtimeTranslationUpstreamException => Error(502, ApiErrorCodes.UpstreamFailure, exception),
        SpeakingValidationException => Error(400, ApiErrorCodes.ValidationFailed, exception),
        SpeakingQuizNotFoundException => Error(404, ApiErrorCodes.NotFound, exception),
        SpeakingSessionNotFoundException => Error(404, ApiErrorCodes.NotFound, exception),
        SpeakingSessionExpiredException => Error(410, ApiErrorCodes.Gone, exception),
        SpeakingSessionInvalidatedException => Error(410, ApiErrorCodes.Gone, exception),
        SpeakingSessionBusyException => Error(409, ApiErrorCodes.Conflict, exception),
        SpeakingSessionLimitException => Error(409, ApiErrorCodes.Conflict, exception),
        SpeakingDependencyUnavailableException => Error(503, ApiErrorCodes.DependencyUnavailable, exception),
        SpeakingUpstreamException => Error(502, ApiErrorCodes.UpstreamFailure, exception),
        QuizNotFoundException => Error(404, ApiErrorCodes.NotFound, exception),
        UnauthorizedAccessException => new ApiError(403, ApiErrorCodes.Forbidden, "You do not have access to this resource."),
        _ => null,
    };

    private static ApiError Error(int statusCode, string code, Exception exception) =>
        new(statusCode, code, exception.Message);
}
