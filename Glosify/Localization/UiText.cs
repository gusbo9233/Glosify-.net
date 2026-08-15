using System.Collections;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Glosify.Localization;

/// <summary>Marker type for the shared English and Swedish learner-interface resources.</summary>
public sealed class UiText;

/// <summary>Only for direct controller unit tests that do not build the service graph.</summary>
internal sealed class PassthroughStringLocalizer<T> : Microsoft.Extensions.Localization.IStringLocalizer<T>
{
    public static PassthroughStringLocalizer<T> Instance { get; } = new();

    public Microsoft.Extensions.Localization.LocalizedString this[string name] => new(name, name, true);

    public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] =>
        new(name, string.Format(System.Globalization.CultureInfo.CurrentCulture, name, arguments), true);

    public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}

/// <summary>
/// Localized UI catalogs are embedded under deterministic manifest names. Select the
/// catalog from the supported display-culture list while delegating other localizers
/// to ASP.NET Core.
/// </summary>
public sealed class UiTextStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly ResourceManagerStringLocalizerFactory _fallback;
    private readonly UiTextStringLocalizer _uiText = new();

    public UiTextStringLocalizerFactory(
        IOptions<LocalizationOptions> options,
        ILoggerFactory loggerFactory)
    {
        _fallback = new ResourceManagerStringLocalizerFactory(options, loggerFactory);
    }

    public IStringLocalizer Create(Type resourceSource) =>
        resourceSource == typeof(UiText) ? _uiText : _fallback.Create(resourceSource);

    public IStringLocalizer Create(string baseName, string location) =>
        baseName.EndsWith($".{nameof(UiText)}", StringComparison.Ordinal)
            ? _uiText
            : _fallback.Create(baseName, location);
}

public sealed class UiTextStringLocalizer : IStringLocalizer<UiText>
{
    private const string BaseName = "Glosify.Resources.Localization.UiText";
    private static readonly ResourceManager English = new(BaseName, typeof(UiText).Assembly);
    private static readonly IReadOnlyDictionary<string, ResourceManager> Localized =
        DisplayCultureCatalog.LocalizedPublicCultures.ToDictionary(
            culture => culture.Name,
            culture => new ResourceManager($"{BaseName}.{culture.Name}", typeof(UiText).Assembly),
            StringComparer.OrdinalIgnoreCase);

    public LocalizedString this[string name]
    {
        get
        {
            var value = CurrentManager.GetString(name, CultureInfo.InvariantCulture)
                ?? English.GetString(name, CultureInfo.InvariantCulture);
            return new LocalizedString(name, value ?? name, value is null);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var value = this[name];
            return new LocalizedString(
                name,
                string.Format(CultureInfo.CurrentCulture, value.Value, arguments),
                value.ResourceNotFound);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var set = CurrentManager.GetResourceSet(
            CultureInfo.InvariantCulture,
            createIfNotExists: true,
            tryParents: false);
        if (set is null)
        {
            yield break;
        }

        foreach (DictionaryEntry entry in set)
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                yield return new LocalizedString(name, value, false);
            }
        }
    }

    private static ResourceManager CurrentManager =>
        Localized.GetValueOrDefault(CultureInfo.CurrentUICulture.Name) ?? English;
}
