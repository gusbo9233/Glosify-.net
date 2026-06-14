using Glosify.Models.Library;

namespace Glosify.Models.ViewModels;

public sealed class BookLibraryViewModel
{
    public IReadOnlyList<BookDocument> Books { get; set; } = [];
}

public sealed class BookReaderViewModel
{
    public BookDocument Book { get; set; } = null!;
    public IReadOnlyList<Quiz> Quizzes { get; set; } = [];
    public Guid? SelectedQuizId { get; set; }
}
