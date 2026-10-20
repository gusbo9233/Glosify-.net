using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Glosify.Services.Language;

public interface ILanguageContext
{
    string? CurrentLanguage { get; }
    IReadOnlyList<string> SupportedLanguages { get; }
    bool TrySetLanguage(string language);
    void Clear();
}

public class CookieLanguageContext : ILanguageContext
{
    private const string CookieName = "glosify.language";
    private const string GuestCookieName = "glosify.guest-language";

    private readonly IHttpContextAccessor _accessor;
    private string? _resolvedLanguage;
    private bool _initialized;

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
            if (_initialized) return _resolvedLanguage;
            var ctx = _accessor.HttpContext;
            if (ctx == null) return null;
            if (ctx.User.Identity?.IsAuthenticated != true && ctx.Request.Cookies.TryGetValue(GuestCookieName, out var guestValue))
                return QuizLanguageCatalog.Find(guestValue)?.Name;
            if (!ctx.Request.Cookies.TryGetValue(CookieName, out var value)) return null;
            return QuizLanguageCatalog.Find(value)?.Name;
        }
    }

    internal async Task InitializeAsync(IQuizLanguagePreferenceService preferences, string userId, CancellationToken cancellationToken)
    {
        if (_initialized) return;
        _resolvedLanguage = CurrentLanguage ?? (await preferences.GetSelectedAsync(userId, cancellationToken))?.Name;
        _initialized = true;
    }

    public bool TrySetLanguage(string language)
    {
        var canonical = QuizLanguageCatalog.Find(language);
        if (canonical is null)
        {
            return false;
        }

        var ctx = _accessor.HttpContext;
        if (ctx == null) return false;

        _resolvedLanguage = canonical.Name;
        _initialized = true;
        var cookieName = ctx.User.Identity?.IsAuthenticated == true ? CookieName : GuestCookieName;
        ctx.Response.Cookies.Append(cookieName, canonical.Name, new CookieOptions
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
        _resolvedLanguage = null;
        _initialized = true;
        var ctx = _accessor.HttpContext;
        if (ctx?.User.Identity?.IsAuthenticated == true) ctx.Response.Cookies.Delete(CookieName);
        else if (ctx is not null)
            ctx.Response.Cookies.Append(GuestCookieName, "", new CookieOptions
            {
                HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddYears(1)
            });
    }
}

/// <summary>Resolve the account fallback once before MVC actions and views read the language.</summary>
public sealed class SelectedLanguageFilter(
    ILanguageContext languages,
    IQuizLanguagePreferenceService preferences) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true
            && user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            && languages is CookieLanguageContext cookieLanguages)
        {
            await cookieLanguages.InitializeAsync(preferences, userId, context.HttpContext.RequestAborted);
        }
        await next();
    }
}
