using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Localization;

namespace Glosify.Localization;

public sealed record DisplayCulture(string Name, string NativeName, string EnglishName)
{
    public CultureInfo CultureInfo { get; } = CultureInfo.GetCultureInfo(Name);
    public bool IsRightToLeft => CultureInfo.TextInfo.IsRightToLeft;
}

public static class DisplayCultureCatalog
{
    public const string DefaultCulture = "en-GB";
    public const string SwedishCulture = "sv-SE";
    public const string ClaimType = "glosify:display_culture";
    public const int StorageMaximumLength = 8;

    private static readonly DisplayCulture[] Cultures =
    [
        new(DefaultCulture, "English", "English"),
        new(SwedishCulture, "Svenska", "Swedish"),
        new("es-419", "Español (Latinoamérica)", "Spanish (Latin America)"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new("fr-FR", "Français", "French"),
        new("ja-JP", "日本語", "Japanese"),
        new("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new("uk-UA", "Українська", "Ukrainian"),
        new("tr-TR", "Türkçe", "Turkish"),
        new("id-ID", "Bahasa Indonesia", "Indonesian"),
        new("vi-VN", "Tiếng Việt", "Vietnamese"),
        new("ar", "العربية", "Arabic"),
    ];

    public static IReadOnlyList<DisplayCulture> All { get; } = Array.AsReadOnly(Cultures);
    public static IReadOnlyList<DisplayCulture> LocalizedPublicCultures { get; } =
        Array.AsReadOnly(Cultures.Where(culture => culture.Name != DefaultCulture).ToArray());
    public static IList<CultureInfo> CultureInfos { get; } =
        Cultures.Select(culture => culture.CultureInfo).ToArray();

    public static string CheckConstraintSql { get; } =
        $"[DisplayCulture] IS NULL OR [DisplayCulture] IN ({string.Join(", ", Cultures.Select(culture => $"'{culture.Name}'"))})";

    public static bool TryCanonicalize(string? value, out string culture)
    {
        var match = Cultures.FirstOrDefault(item =>
            string.Equals(item.Name, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        culture = match?.Name ?? string.Empty;
        return match is not null;
    }

    public static bool IsSupported(string? value) => TryCanonicalize(value, out _);

    public static bool IsLocalizedPublicCulture(string? value) =>
        TryCanonicalize(value, out var culture) && culture != DefaultCulture;
}

public sealed class DisplayCultureRouteConstraint : IRouteConstraint
{
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out var value)
        && DisplayCultureCatalog.IsLocalizedPublicCulture(Convert.ToString(value, CultureInfo.InvariantCulture));
}

public sealed class UnsupportedDisplayCultureRouteConstraint : IRouteConstraint
{
    private static readonly Regex CultureShape = new(
        "^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,4})?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        var value = Convert.ToString(values.GetValueOrDefault(routeKey), CultureInfo.InvariantCulture);
        return value is not null
            && CultureShape.IsMatch(value)
            && !DisplayCultureCatalog.IsSupported(value);
    }
}

public sealed class LocalizedPublicRequestCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var value = Convert.ToString(httpContext.Request.RouteValues["culture"], CultureInfo.InvariantCulture);
        return Task.FromResult(DisplayCultureCatalog.IsLocalizedPublicCulture(value)
            && DisplayCultureCatalog.TryCanonicalize(value, out var culture)
                ? new ProviderCultureResult(culture, culture)
                : null);
    }
}

/// <summary>
/// Reads the persisted display culture from the authenticated Identity principal.
/// Authentication intentionally runs before request localization so this provider can
/// honor an account preference without querying the user table on every request.
/// </summary>
public sealed class DisplayCultureClaimRequestCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var value = httpContext.User.FindFirstValue(DisplayCultureCatalog.ClaimType);
        return Task.FromResult(DisplayCultureCatalog.TryCanonicalize(value, out var culture)
            ? new ProviderCultureResult(culture, culture)
            : null);
    }
}
