using Glosify.Data;
using Glosify.Models;
using Glosify.Models.LanguageConfig;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Glosify.Tests;

public class DictionaryProbeTests
{
    private readonly ITestOutputHelper _output;

    public DictionaryProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> ConfigProbes => new[]
    {
        new object[] { new UkrainianDictionaryConfig(), "будинок", "Noun" },
        new object[] { new UkrainianDictionaryConfig(), "робити", "Verb" },
        new object[] { new UkrainianDictionaryConfig(), "великий", "Adjective" },
        new object[] { new UkrainianDictionaryConfig(), "ви", "Pronoun" },
        new object[] { new GermanDictionaryConfig(), "Hund", "Noun" },
        new object[] { new GermanDictionaryConfig(), "machen", "Verb" },
        new object[] { new GermanDictionaryConfig(), "klein", "Adjective" },
        new object[] { new GermanDictionaryConfig(), "ich", "Pronoun" },
        new object[] { new EstonianDictionaryConfig(), "maja", "Noun" },
        new object[] { new EstonianDictionaryConfig(), "tegema", "Verb" },
        new object[] { new EstonianDictionaryConfig(), "suur", "Adjective" },
        new object[] { new EstonianDictionaryConfig(), "mina", "Pronoun" },
        new object[] { new PolishDictionaryConfig(), "dom", "Noun" },
        new object[] { new PolishDictionaryConfig(), "robić", "Verb" },
        new object[] { new PolishDictionaryConfig(), "dobry", "Adjective" },
        new object[] { new PolishDictionaryConfig(), "ja", "Pronoun" },
    };

    [Theory]
    [MemberData(nameof(ConfigProbes))]
    public async Task ConfigSlotsResolveAgainstRealData(ILanguageDictionaryConfig config, string word, string pos)
    {
        using var ctx = CreateContext();
        var entry = await FindEntryAsync(ctx, config.LangCode, word, pos);
        Assert.NotNull(entry);

        var variants = WordDetailViewModel.ReadVariants(entry.Variants);
        if (pos == "Pronoun")
        {
            variants = WordDetailViewModel.FilterPronounParadigm(variants, word);
        }
        var wordClass = config.GetWordClass(pos);
        Assert.NotNull(wordClass);
        Assert.NotEmpty(wordClass.SlotGroups);

        var emptySlots = new List<string>();
        var filledSlots = new List<string>();

        foreach (var group in wordClass.SlotGroups)
        {
            foreach (var slot in group.Slots)
            {
                var match = variants.Where(v => !string.IsNullOrWhiteSpace(v.Form) && v.Form != "-")
                    .Where(v => slot.Tags.All(t => v.HasAnyTag(t)))
                    .OrderBy(v => v.Tags.Count).Select(v => v.Form).FirstOrDefault();
                var key = $"{group.Heading}/{slot.Label}";
                if (string.IsNullOrEmpty(match)) emptySlots.Add(key);
                else filledSlots.Add($"{key} → {match}");
            }
        }

        _output.WriteLine($"=== {config.LangCode} {pos} '{word}' ===");
        _output.WriteLine($"  Filled ({filledSlots.Count}):");
        foreach (var s in filledSlots) _output.WriteLine($"    {s}");
        _output.WriteLine($"  Empty ({emptySlots.Count}):");
        foreach (var s in emptySlots) _output.WriteLine($"    {s}");

        Assert.True(filledSlots.Count > emptySlots.Count,
            $"More empty slots ({emptySlots.Count}) than filled ({filledSlots.Count}) — config tags don't match real data");
    }

    private static async Task<DictionaryEntry?> FindEntryAsync(
        GlosifyContext ctx, string langCode, string word, string expectedPos)
    {
        var entries = await ctx.DictionaryEntries.AsNoTracking()
            .Where(e => e.LangCode == langCode && e.Word == word)
            .ToListAsync();

        return entries.FirstOrDefault(e =>
            WordDetailViewModel.ReadProperties(e.Properties)
                .Any(p => string.Equals(p.Key, "pos", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Value, expectedPos, StringComparison.OrdinalIgnoreCase)))
            ?? entries.FirstOrDefault();
    }

    private static GlosifyContext CreateContext()
    {
        var cs = LoadConnectionString();
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(cs)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new GlosifyContext(options);
    }

    private static string LoadConnectionString()
    {
        var repoRoot = FindRepoRoot();
        var config = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(repoRoot, "Glosify"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        return config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection missing");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Glosify-.net.sln"))
            && !Directory.Exists(Path.Combine(dir.FullName, "Glosify")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
