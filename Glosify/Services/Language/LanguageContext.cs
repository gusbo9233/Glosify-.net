namespace Glosify.Services
{
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

        private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "Estonian",
            "German",
            "Polish",
            "Ukrainian"
        };

        private readonly IHttpContextAccessor _accessor;

        public CookieLanguageContext(IHttpContextAccessor accessor)
        {
            _accessor = accessor;
        }

        public IReadOnlyList<string> SupportedLanguages { get; } =
            new[] { "Estonian", "German", "Polish", "Ukrainian" };

        public string? CurrentLanguage
        {
            get
            {
                var ctx = _accessor.HttpContext;
                if (ctx == null) return null;
                if (!ctx.Request.Cookies.TryGetValue(CookieName, out var value)) return null;
                return Allowed.TryGetValue(value, out var canonical) ? canonical : null;
            }
        }

        public bool HasLanguage => CurrentLanguage != null;

        public bool TrySetLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language) || !Allowed.TryGetValue(language, out var canonical))
            {
                return false;
            }

            var ctx = _accessor.HttpContext;
            if (ctx == null) return false;

            ctx.Response.Cookies.Append(CookieName, canonical, new CookieOptions
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
}
