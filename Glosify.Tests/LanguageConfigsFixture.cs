using Glosify.Models.LanguageConfig;

namespace Glosify.Tests;

internal static class LanguageConfigsFixture
{
    public static IEnumerable<ILanguageDictionaryConfig> All => new ILanguageDictionaryConfig[]
    {
        new GermanDictionaryConfig(),
        new EstonianDictionaryConfig(),
        new UkrainianDictionaryConfig(),
        new PolishDictionaryConfig(),
    };
}
