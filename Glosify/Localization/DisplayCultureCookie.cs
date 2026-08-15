using Microsoft.AspNetCore.Localization;

namespace Glosify.Localization;

public static class DisplayCultureCookie
{
    public static void Append(HttpResponse response, HttpRequest request, string culture)
    {
        response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = request.IsHttps,
            });
    }
}
