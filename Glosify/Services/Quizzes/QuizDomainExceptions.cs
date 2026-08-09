namespace Glosify.Services.Quizzes;

public sealed class CollectionParentNotFoundException()
    : InvalidOperationException("Parent collection not found for this user and language.");

public sealed class CollectionNameConflictException()
    : InvalidOperationException("A collection with this name already exists here.");

public sealed class QuizCollectionNotFoundException()
    : InvalidOperationException("Collection not found for this user and language.");
