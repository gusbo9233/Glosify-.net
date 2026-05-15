using Glosify.Models.LanguageConfig;
using Xunit;

namespace Glosify.Tests;

public class GeminiLanguageConfigTests
{
    public static IEnumerable<object[]> NounCaseConfigs()
    {
        yield return
        [
            new GermanDictionaryConfig(),
            new[]
            {
                V("Haus", "nominative", "singular"),
                V("Hauses", "genitive", "singular"),
                V("Haus", "dative", "singular"),
                V("Haus", "accusative", "singular"),
                V("Haeuser", "nominative", "plural"),
                V("Haeuser", "genitive", "plural"),
                V("Haeusern", "dative", "plural"),
                V("Haeuser", "accusative", "plural"),
            }
        ];

        yield return
        [
            new EstonianDictionaryConfig(),
            new[]
            {
                V("maja", "nominative", "singular"),
                V("maja", "genitive", "singular"),
                V("maja", "partitive", "singular"),
                V("majad", "nominative", "plural"),
                V("majade", "genitive", "plural"),
                V("maju", "partitive", "plural"),
                V("majasse", "illative", "singular"),
                V("majas", "inessive", "singular"),
                V("majast", "elative", "singular"),
                V("majale", "allative", "singular"),
                V("majal", "adessive", "singular"),
                V("majalt", "ablative", "singular"),
                V("majaks", "translative", "singular"),
                V("majani", "terminative", "singular"),
                V("majana", "essive", "singular"),
                V("majata", "abessive", "singular"),
                V("majaga", "comitative", "singular"),
            }
        ];

        yield return
        [
            new PolishDictionaryConfig(),
            new[]
            {
                V("dom", "nominative", "singular"),
                V("domu", "genitive", "singular"),
                V("domowi", "dative", "singular"),
                V("dom", "accusative", "singular"),
                V("domem", "instrumental", "singular"),
                V("domu", "locative", "singular"),
                V("domu", "vocative", "singular"),
                V("domy", "nominative", "plural"),
                V("domow", "genitive", "plural"),
                V("domom", "dative", "plural"),
                V("domy", "accusative", "plural"),
                V("domami", "instrumental", "plural"),
                V("domach", "locative", "plural"),
                V("domy", "vocative", "plural"),
            }
        ];

        yield return
        [
            new UkrainianDictionaryConfig(),
            new[]
            {
                V("dim", "nominative", "singular"),
                V("domu", "genitive", "singular"),
                V("domu", "dative", "singular"),
                V("dim", "accusative", "singular"),
                V("domom", "instrumental", "singular"),
                V("domi", "locative", "singular"),
                V("dome", "vocative", "singular"),
                V("domy", "nominative", "plural"),
                V("domiv", "genitive", "plural"),
                V("domam", "dative", "plural"),
                V("domy", "accusative", "plural"),
                V("domamy", "instrumental", "plural"),
                V("domakh", "locative", "plural"),
                V("domy", "vocative", "plural"),
            }
        ];
    }

    [Theory]
    [MemberData(nameof(NounCaseConfigs))]
    public void NounCaseSlotsMatchGeminiStyleTags(
        ILanguageDictionaryConfig config,
        IReadOnlyList<WordDetailVariantViewModel> variants)
    {
        var wordClass = config.GetWordClass("Noun");

        Assert.NotNull(wordClass);
        Assert.Empty(FindEmptySlots(wordClass!, variants));
    }

    [Fact]
    public void GeminiPronounConfigsDoNotUseLegacyBundledFiltering()
    {
        Assert.False(new PolishDictionaryConfig().BundlesPronounParadigm);
        Assert.False(new UkrainianDictionaryConfig().BundlesPronounParadigm);
    }

    [Fact]
    public void PolishPastVerbSlotsPreservePersonAndPluralGender()
    {
        var wordClass = new PolishDictionaryConfig().GetWordClass("Verb");
        var variants = new[]
        {
            V("byłem", "past", "first-person", "singular", "masculine"),
            V("byłam", "past", "first-person", "singular", "feminine"),
            V("byłeś", "past", "second-person", "singular", "masculine"),
            V("byłaś", "past", "second-person", "singular", "feminine"),
            V("był", "past", "third-person", "singular", "masculine"),
            V("była", "past", "third-person", "singular", "feminine"),
            V("było", "past", "third-person", "singular", "neuter"),
            V("byliśmy", "past", "first-person", "plural", "masculine-personal"),
            V("byłyśmy", "past", "first-person", "plural", "non-masculine-personal"),
            V("byliście", "past", "second-person", "plural", "masculine-personal"),
            V("byłyście", "past", "second-person", "plural", "non-masculine-personal"),
            V("byli", "past", "third-person", "plural", "masculine-personal"),
            V("były", "past", "third-person", "plural", "non-masculine-personal"),
        };

        Assert.NotNull(wordClass);
        Assert.DoesNotContain(
            FindEmptySlots(wordClass!, variants),
            slot => slot.StartsWith("Past ", StringComparison.Ordinal));
    }

    [Fact]
    public void UkrainianPronounCasesMatchGeminiParadigmForObliqueSurfaceForm()
    {
        var wordClass = new UkrainianDictionaryConfig().GetWordClass("Pronoun");
        var variants = new[]
        {
            V("він", "nominative", "third-person", "singular", "masculine"),
            V("його", "genitive", "third-person", "singular", "masculine"),
            V("йому", "dative", "third-person", "singular", "masculine"),
            V("його", "accusative", "third-person", "singular", "masculine"),
            V("ним", "instrumental", "third-person", "singular", "masculine"),
            V("ньому", "locative", "third-person", "singular", "masculine"),
        };

        Assert.NotNull(wordClass);
        Assert.Empty(FindEmptySlots(wordClass!, variants));
    }

    private static WordDetailVariantViewModel V(string form, params string[] tags)
        => new(form, tags);

    private static IReadOnlyList<string> FindEmptySlots(
        WordClassConfig config,
        IReadOnlyList<WordDetailVariantViewModel> variants)
    {
        return config.SlotGroups
            .SelectMany(group => group.Slots.Select(slot => new { group.Heading, slot }))
            .Where(item => !variants.Any(variant =>
                !string.IsNullOrWhiteSpace(variant.Form)
                && item.slot.Tags.All(tag => variant.HasAnyTag(tag))))
            .Select(item => $"{item.Heading}/{item.slot.Label}")
            .ToList();
    }
}
