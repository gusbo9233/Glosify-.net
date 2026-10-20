using System.Text.Json;
using Glosify.Services.Language;

namespace Glosify.Services.Guest;

public sealed record GuestLibrary(string Key, string Title, string Action, string SampleTitle, string ReturnUrl);
public sealed record GuestCard(string Target, string Source);

/// <summary>Original, bundled examples. Never resolves a user, database ID, or paid provider.</summary>
public sealed partial class GuestSamples
{
    private static readonly IReadOnlyDictionary<string, string[]> Content = LoadContent();
    private readonly string[] _phrases;
    public QuizLanguage Language { get; }
    public string SourceLanguage => Language.Code == "en" ? "Spanish" : "English";
    public string SourceCode => Language.Code == "en" ? "es" : "en";
    public string TargetLanguage => Language.IsLanguageLearning ? Language.Name : "English";
    public string ContentCode => Language.IsLanguageLearning ? Language.Code : "free";
    public string PdfUrl => $"/samples/starter-{ContentCode}.pdf";
    public string Title => Language.IsLanguageLearning ? $"First words in {Language.Name}" : "Our solar system";
    public IReadOnlyList<GuestCard> Cards { get; }

    private GuestSamples(QuizLanguage language)
    {
        Language = language;
        _phrases = Content[language.IsLanguageLearning ? language.Code : "en"];
        Cards = language.IsLanguageLearning
            ? Array.AsReadOnly(_phrases.Take(5).Select((phrase, index) => new GuestCard(phrase, Content[SourceCode][index])).ToArray())
            : Array.AsReadOnly<GuestCard>([
                new("Mercury", "Which planet is closest to the Sun?"),
                new("Venus", "Which planet is the hottest?"),
                new("Earth", "Which planet do we live on?"),
                new("Mars", "Which planet is called the Red Planet?"),
                new("Jupiter", "Which planet is the largest?")]);
    }

    public static GuestSamples For(string? language) => new(QuizLanguageCatalog.Find(language) ?? QuizLanguageCatalog.Find("es")!);
    public static GuestSamples For(HttpContext context) => For(context.RequestServices.GetRequiredService<ILanguageContext>().CurrentLanguage);

    private static IReadOnlyDictionary<string, string[]> LoadContent()
    {
        using var stream = typeof(GuestSamples).Assembly.GetManifestResourceStream("Glosify.Guest.StarterContent.json")
            ?? throw new InvalidOperationException("Bundled guest content is missing.");
        var content = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
        foreach (var language in QuizLanguageCatalog.LanguageLearning)
            if (!content.TryGetValue(language.Code, out var phrases) || phrases.Length != 7 || phrases.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException($"Incomplete guest content for {language.Code}.");
        return content;
    }

    public static GuestLibrary? Find(string section) => section switch
    {
        "books" => new(section, "Books", "Upload a book", "First words", "/Books"),
        "quiz" => new(section, "Quizzes", "Create a quiz", "Starter phrases", "/Quiz"),
        "anki" => new(section, "Anki", "Add cards", "Everyday phrases", "/Anki"),
        "transcripts" => new(section, "Transcripts", "Save your own transcripts", "A first conversation", "/Transcripts"),
        "translations" => new(section, "Translations", "Save your own translations", "A friendly greeting", "/Translations"),
        _ => null
    };

    public static GuestLibrary Library(string section) => Find(section) ?? throw new ArgumentException("Unknown sample section.", nameof(section));
}
