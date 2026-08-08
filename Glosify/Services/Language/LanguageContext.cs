namespace Glosify.Services.Language;

public interface ILanguageContext
{
    string? CurrentLanguage { get; }
    bool HasLanguage { get; }
    IReadOnlyList<string> SupportedLanguages { get; }
    bool TrySetLanguage(string language);
    void Clear();
}

public class CookieLanguageContext : ILanguageContext
{
    private const string CookieName = "glosify.language";

    private readonly IHttpContextAccessor _accessor;

    public CookieLanguageContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public IReadOnlyList<string> SupportedLanguages { get; } =
        QuizLanguageCatalog.All.Select(language => language.Name).ToArray();

    public string? CurrentLanguage
    {
        get
        {
            var ctx = _accessor.HttpContext;
            if (ctx == null) return null;
            if (!ctx.Request.Cookies.TryGetValue(CookieName, out var value)) return null;
            return QuizLanguageCatalog.Find(value)?.Name;
        }
    }

    public bool HasLanguage => CurrentLanguage != null;

    public bool TrySetLanguage(string language)
    {
        var canonical = QuizLanguageCatalog.Find(language);
        if (canonical is null)
        {
            return false;
        }

        var ctx = _accessor.HttpContext;
        if (ctx == null) return false;

        ctx.Response.Cookies.Append(CookieName, canonical.Name, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps
        });

        return true;
    }

    public void Clear()
    {
        var ctx = _accessor.HttpContext;
        ctx?.Response.Cookies.Delete(CookieName);
    }
}
