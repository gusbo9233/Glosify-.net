
namespace Glosify.Models.ViewModels;

public sealed class BookLibraryViewModel
{
    public IReadOnlyList<BookCard> Books { get; set; } = [];
}

public sealed class BookReaderViewModel
{
    public BookCard Book { get; set; } = null!;
    public IReadOnlyList<QuizCard> Quizzes { get; set; } = [];
    public Guid? SelectedQuizId { get; set; }
}
