using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Language;

public sealed record QuizLanguage(string Code, string Name);

public static class QuizLanguageCatalog
{
    private static readonly QuizLanguage[] Languages =
    [
        new("et", "Estonian"),
        new("de", "German"),
        new("pl", "Polish"),
        new("uk", "Ukrainian"),
    ];

    public static IReadOnlyList<QuizLanguage> All { get; } = Languages;

    public static QuizLanguage? Find(string? value) =>
        Languages.FirstOrDefault(language =>
            string.Equals(language.Code, value?.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(language.Name, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public interface IQuizLanguagePreferenceService
{
    Task<QuizLanguage?> GetSelectedAsync(string userId, CancellationToken cancellationToken = default);
    Task<QuizLanguage> SetSelectedAsync(string userId, string language, CancellationToken cancellationToken = default);
    Task ClearAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class QuizLanguagePreferenceService : IQuizLanguagePreferenceService
{
    private readonly GlosifyContext _context;

    public QuizLanguagePreferenceService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<QuizLanguage?> GetSelectedAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var code = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.SelectedQuizLanguageCode)
            .SingleOrDefaultAsync(cancellationToken);
        return QuizLanguageCatalog.Find(code);
    }

    public async Task<QuizLanguage> SetSelectedAsync(
        string userId,
        string language,
        CancellationToken cancellationToken = default)
    {
        var selected = QuizLanguageCatalog.Find(language)
            ?? throw new ArgumentException("Choose a supported quiz language.", nameof(language));
        var user = await _context.Users.SingleAsync(user => user.Id == userId, cancellationToken);
        user.SelectedQuizLanguageCode = selected.Code;
        await _context.SaveChangesAsync(cancellationToken);
        return selected;
    }

    public async Task ClearAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.SingleAsync(user => user.Id == userId, cancellationToken);
        user.SelectedQuizLanguageCode = null;
        await _context.SaveChangesAsync(cancellationToken);
    }
}
