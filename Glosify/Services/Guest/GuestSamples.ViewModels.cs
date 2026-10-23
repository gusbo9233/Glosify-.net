using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Anki;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Translator;

namespace Glosify.Services.Guest;

public sealed partial class GuestSamples
{
    public static readonly Guid SampleId = Guid.Parse("d528b31d-dfb9-4d51-90e9-18cb316575ea");
    private static readonly DateTimeOffset Created = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    public QuizCard Quiz => new(SampleId, Language.IsLanguageLearning ? $"{Language.Name} essentials" : Title, SourceLanguage, Language.Name, "Ready", Created, null, false);
    public BookCard Book => new(SampleId, Title, 1, SourceLanguage, "Ready", null, Created);
    private IReadOnlyList<WordRow> Words => Cards.Select((card, index) => new WordRow($"sample-{index}", card.Target, card.Source)).ToArray();
    private string OriginalTranslation => Language.IsLanguageLearning ? $"{_phrases[0]}! {_phrases[5]} {_phrases[6]}" : "Mercury is closest to the Sun. Jupiter is the largest planet.";
    private string TranslatedText => Language.IsLanguageLearning ? $"{Content[SourceCode][0]}! {Content[SourceCode][5]} {Content[SourceCode][6]}" : OriginalTranslation;
    private string[] TranscriptLines => Language.IsLanguageLearning
        ? [_phrases[3], _phrases[0], _phrases[5], _phrases[6], _phrases[1], _phrases[4]]
        : ["Which planet is closest to the Sun?", "Mercury.", "Which planet is the largest?", "Jupiter."];
    private string[] TranslatedLines => Language.IsLanguageLearning
        ? [Content[SourceCode][3], Content[SourceCode][0], Content[SourceCode][5], Content[SourceCode][6], Content[SourceCode][1], Content[SourceCode][4]]
        : TranscriptLines;
    private string ConversationTitle => Language.IsLanguageLearning ? $"A first conversation in {Language.Name}" : "Talking about planets";

    public object Index(string section) => section switch
    {
        "books" => new BookLibraryViewModel { Books = [Book] },
        "quiz" => new QuizIndexViewModel { VisibleQuizzes = [Quiz], Presentation = new(Language.Name, Language.Name, !Language.IsLanguageLearning, "Your quizzes", $"Explore a {Language.Name} sample quiz.", Language.Name, "") },
        "anki" => Anki(),
        "transcripts" => new TranscriptLibraryViewModel { Library = new([new(SampleId, ConversationTitle, ContentCode, "source", Created, Created.AddMinutes(1), TranscriptLines.Length)], 1, 24, 1) },
        "translations" => new SavedTranslationLibraryViewModel { Library = new(ContentCode, [new(SampleId, ContentCode, ConversationTitle, OriginalTranslation, TranslatedText, 1, Created, Created)], 1, 24, 1, 1) },
        _ => throw new ArgumentException("Unknown sample section.", nameof(section))
    };

    public object Detail(string section, string? stream) => section switch
    {
        "books" => new BookReaderViewModel { Book = Book, Quizzes = [Quiz] },
        "quiz" => new QuizWorkspaceViewModel { SelectedQuiz = Quiz, Words = Words },
        "anki" => Anki(),
        "transcripts" => Transcript(stream),
        "translations" => new SavedTranslationSessionDetailViewModel { Session = new(SampleId, ContentCode, ConversationTitle, Created, Created, [new(SampleId, ContentCode, ContentCode, SourceCode, null, OriginalTranslation, TranslatedText, Created)], 1, 24, 1) },
        _ => throw new ArgumentException("Unknown sample section.", nameof(section))
    };

    private TranscriptDetailViewModel Transcript(string? stream)
    {
        var translation = stream == "translation";
        var selected = translation ? "translation" : "source";
        var segments = (translation ? TranslatedLines : TranscriptLines).Select((text, index) => new RealtimeTranslationTranscriptSegment
        {
            TranscriptId = SampleId, Sequence = index + 1, Text = text, Stream = selected,
            CapturedAt = Created.AddSeconds(index * 8), Speaker = index % 2 == 0 ? "Alex" : "Sam"
        }).ToArray();
        return new() { Transcript = new(SampleId, ConversationTitle, ContentCode, "source", Created, Created.AddMinutes(1), segments, 1, 100, segments.Length, false, selected, TranscriptLines.Length, TranslatedLines.Length) };
    }

    public QuizSettingsViewModel Settings() => new()
    {
        SelectedQuiz = Quiz, Words = Words, AvailableWordCount = Cards.Count,
        Presentation = QuizSettingsPresentation.Create(Quiz, Cards.Count, 0, Cards.Count, Quiz.Name, SourceLanguage, TargetLanguage, "Words")
    };

    public AnkiIndexViewModel Anki() => new()
    {
        TargetLanguage = TargetLanguage, Direction = PracticeDirection.SourceToTarget,
        Workspace = new(new AnkiCollection { Id = SampleId, Name = Title, TargetLanguage = TargetLanguage }, [],
            [new Quiz { Id = SampleId, Name = Quiz.Name, SourceLanguage = SourceLanguage, TargetLanguage = TargetLanguage }],
            Words.Select(word => new AnkiAvailableItem(SampleId, Quiz.Name, SourceLanguage, "words", word.Id, word.Lemma, word.Translation, true)).ToArray()),
        Study = new()
        {
            TargetLanguage = TargetLanguage, AnswerRevealed = true,
            State = new(new(SampleId, Title, SampleId, "", Cards[0].Source, Cards[0].Target, SourceLanguage, TargetLanguage, "words", PracticeDirection.SourceToTarget, "new", new Dictionary<string, string>(), 0, Cards.Count), null, "UTC", PracticeDirection.SourceToTarget),
            Statistics = new(new(0, Cards.Count, 0, Cards.Count, 0), 0, 0, [], [])
        }
    };

    public FlashcardQuizViewModel Flashcards(string? direction = null) => new()
    {
        SelectedQuiz = Quiz, QuizId = SampleId, CurrentCard = new() { Prompt = PracticeDirection.IsTargetToSource(direction) ? Cards[0].Target : Cards[0].Source, Answer = PracticeDirection.IsTargetToSource(direction) ? Cards[0].Source : Cards[0].Target },
        TotalCards = Cards.Count, CurrentCardNumber = 1, WordCount = Cards.Count, IsAnswerRevealed = true,
        PracticeDirection = PracticeDirection.Normalize(direction), PromptLanguage = PracticeDirection.PromptLanguage(direction, SourceLanguage, TargetLanguage), AnswerLanguage = PracticeDirection.AnswerLanguage(direction, SourceLanguage, TargetLanguage), DirectionLabel = PracticeDirection.Label(direction, SourceLanguage, TargetLanguage)
    };
    public TypingQuizViewModel Typing(string? direction = null) => new()
    {
        SelectedQuiz = Quiz, QuizId = SampleId, CurrentWord = new() { Prompt = PracticeDirection.IsTargetToSource(direction) ? Cards[0].Target : Cards[0].Source },
        TotalWords = Cards.Count, CurrentWordNumber = 1, WordCount = Cards.Count,
        PracticeDirection = PracticeDirection.Normalize(direction), PromptLanguage = PracticeDirection.PromptLanguage(direction, SourceLanguage, TargetLanguage), AnswerLanguage = PracticeDirection.AnswerLanguage(direction, SourceLanguage, TargetLanguage), DirectionLabel = PracticeDirection.Label(direction, SourceLanguage, TargetLanguage)
    };
}
