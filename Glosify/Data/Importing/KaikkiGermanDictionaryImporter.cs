using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Data.Importing;

public sealed class KaikkiGermanDictionaryImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;

    public KaikkiGermanDictionaryImporter(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<KaikkiImportResult> ImportAsync(KaikkiImportOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.Path))
        {
            throw new FileNotFoundException("Could not find the Kaikki German JSONL file.", options.Path);
        }

        if (options.ApplyMigrations && !options.DryRun)
        {
            await _context.Database.MigrateAsync(cancellationToken);
        }

        var result = new KaikkiImportResult();
        var batch = new List<DictionaryEntry>(options.BatchSize);
        var checkpoint = options.Resume && !string.IsNullOrWhiteSpace(options.CheckpointPath)
            ? await ReadCheckpointAsync(options.CheckpointPath, cancellationToken)
            : null;
        var resumeAfterLine = checkpoint?.LinesRead ?? 0;

        if (resumeAfterLine > 0)
        {
            result.LinesRead = resumeAfterLine;
            result.Parsed = checkpoint?.Parsed ?? 0;
            result.Inserted = checkpoint?.Inserted ?? 0;
            result.Skipped = checkpoint?.Skipped ?? 0;
            Console.WriteLine($"Resuming from checkpoint after line {resumeAfterLine:n0}.");
        }

        using var stream = File.OpenRead(options.Path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? line;
        var currentLine = 0;
        var parsedThisRun = 0;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentLine++;
            if (currentLine <= resumeAfterLine)
            {
                continue;
            }

            result.LinesRead = currentLine;

            if (string.IsNullOrWhiteSpace(line))
            {
                result.Skipped++;
                await WriteCheckpointAsync(options, result, cancellationToken);
                continue;
            }

            var entry = TryMapEntry(line);
            if (entry == null)
            {
                result.Skipped++;
                await WriteCheckpointAsync(options, result, cancellationToken);
                continue;
            }

            result.Parsed++;
            parsedThisRun++;
            if (options.DryRun)
            {
                PrintDryRunEntry(entry, parsedThisRun);
                await WriteCheckpointAsync(options, result, cancellationToken);
            }
            else
            {
                batch.Add(entry);
                if (batch.Count >= options.BatchSize)
                {
                    result.Inserted += await InsertBatchAsync(batch, cancellationToken);
                    batch.Clear();
                    await WriteCheckpointAsync(options, result, cancellationToken);
                    PrintProgress(result);
                }
            }

            if (options.Limit.HasValue && parsedThisRun >= options.Limit.Value)
            {
                break;
            }
        }

        if (!options.DryRun && batch.Count > 0)
        {
            result.Inserted += await InsertBatchAsync(batch, cancellationToken);
            await WriteCheckpointAsync(options, result, cancellationToken);
            PrintProgress(result);
        }
        else
        {
            await WriteCheckpointAsync(options, result, cancellationToken);
        }

        return result;
    }

    private static async Task<KaikkiImportCheckpoint?> ReadCheckpointAsync(string checkpointPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(checkpointPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(checkpointPath);
        return await JsonSerializer.DeserializeAsync<KaikkiImportCheckpoint>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteCheckpointAsync(KaikkiImportOptions options, KaikkiImportResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.CheckpointPath))
        {
            return;
        }

        var checkpoint = new KaikkiImportCheckpoint
        {
            SourcePath = Path.GetFullPath(options.Path),
            LinesRead = result.LinesRead,
            Parsed = result.Parsed,
            Inserted = result.Inserted,
            Skipped = result.Skipped,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var directory = Path.GetDirectoryName(options.CheckpointPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(options.CheckpointPath);
        await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken);
    }

    private async Task<int> InsertBatchAsync(List<DictionaryEntry> batch, CancellationToken cancellationToken)
    {
        var hashes = batch.Select(entry => entry.SourceHash).ToArray();
        var existing = await _context.DictionaryEntries
            .Where(entry => hashes.Contains(entry.SourceHash))
            .Select(entry => entry.SourceHash)
            .ToListAsync(cancellationToken);

        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var newEntries = batch
            .Where(entry => !existingSet.Contains(entry.SourceHash))
            .ToArray();

        if (newEntries.Length == 0)
        {
            return 0;
        }

        _context.DictionaryEntries.AddRange(newEntries);
        await _context.SaveChangesAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return newEntries.Length;
    }

    private static DictionaryEntry? TryMapEntry(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var word = ReadString(root, "word");
            if (string.IsNullOrWhiteSpace(word))
            {
                return null;
            }

            var sourcePos = ReadString(root, "pos");
            var partOfSpeech = NormalizePartOfSpeech(sourcePos);
            var description = ReadFirstGloss(root);
            var exampleSentence = ReadFirstExample(root);
            var variants = ReadForms(root);
            var properties = ReadProperties(root, sourcePos, partOfSpeech);

            return new DictionaryEntry
            {
                Id = Guid.NewGuid(),
                SourceHash = Hash(line),
                Word = word,
                Language = ReadString(root, "lang", "German"),
                LangCode = ReadString(root, "lang_code", "de"),
                PartOfSpeech = partOfSpeech,
                Properties = properties.ToJsonString(JsonOptions),
                Variants = variants.ToJsonString(JsonOptions),
                Description = description,
                ExampleSentence = exampleSentence,
                ImportedAt = DateTimeOffset.UtcNow
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject ReadProperties(JsonElement root, string sourcePos, string partOfSpeech)
    {
        var properties = new JsonObject
        {
            ["pos"] = partOfSpeech,
            ["source_pos"] = sourcePos
        };

        AddIfPresent(properties, "etymology_number", root, "etymology_number");
        AddIfPresent(properties, "head", root, "head_templates", firstArrayExpansion: true);

        var tags = ReadTagUnion(root);
        if (tags.Count > 0)
        {
            properties["tags"] = new JsonArray(tags.Select(tag => JsonValue.Create(tag)).ToArray<JsonNode?>());
        }

        return properties;
    }

    private static JsonArray ReadForms(JsonElement root)
    {
        var variants = new JsonArray();
        if (!root.TryGetProperty("forms", out var forms) || forms.ValueKind != JsonValueKind.Array)
        {
            return variants;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var formElement in forms.EnumerateArray())
        {
            if (formElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var form = ReadString(formElement, "form");
            if (string.IsNullOrWhiteSpace(form) || IsTemplateForm(form))
            {
                continue;
            }

            var tags = ReadStringArray(formElement, "tags")
                .Where(tag => !IsTemplateTag(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var key = $"{form}\u001f{string.Join(",", tags)}";
            if (!seen.Add(key))
            {
                continue;
            }

            var variant = new JsonObject
            {
                ["form"] = form
            };

            if (tags.Length > 0)
            {
                variant["tags"] = new JsonArray(tags.Select(tag => JsonValue.Create(tag)).ToArray<JsonNode?>());
            }

            var source = ReadString(formElement, "source");
            if (!string.IsNullOrWhiteSpace(source))
            {
                variant["source"] = source;
            }

            variants.Add(variant);
        }

        return variants;
    }

    private static string? ReadFirstGloss(JsonElement root)
    {
        if (!root.TryGetProperty("senses", out var senses) || senses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var sense in senses.EnumerateArray())
        {
            var glosses = ReadStringArray(sense, "glosses");
            if (glosses.Count > 0)
            {
                return string.Join("; ", glosses);
            }
        }

        return null;
    }

    private static string? ReadFirstExample(JsonElement root)
    {
        if (!root.TryGetProperty("senses", out var senses) || senses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var sense in senses.EnumerateArray())
        {
            if (!sense.TryGetProperty("examples", out var examples) || examples.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var example in examples.EnumerateArray())
            {
                var text = ReadString(example, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static List<string> ReadTagUnion(JsonElement root)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in ReadStringArray(root, "tags").Concat(ReadStringArray(root, "raw_tags")))
        {
            if (!IsTemplateTag(tag))
            {
                tags.Add(tag);
            }
        }

        if (root.TryGetProperty("senses", out var senses) && senses.ValueKind == JsonValueKind.Array)
        {
            foreach (var sense in senses.EnumerateArray())
            {
                foreach (var tag in ReadStringArray(sense, "tags").Concat(ReadStringArray(sense, "raw_tags")))
                {
                    if (!IsTemplateTag(tag))
                    {
                        tags.Add(tag);
                    }
                }
            }
        }

        return tags.OrderBy(tag => tag).ToList();
    }

    private static void AddIfPresent(JsonObject properties, string propertyName, JsonElement root, string sourceName, bool firstArrayExpansion = false)
    {
        if (!root.TryGetProperty(sourceName, out var value))
        {
            return;
        }

        if (firstArrayExpansion && value.ValueKind == JsonValueKind.Array)
        {
            var first = value.EnumerateArray().FirstOrDefault();
            var expansion = ReadString(first, "expansion");
            if (!string.IsNullOrWhiteSpace(expansion))
            {
                properties[propertyName] = expansion;
            }

            return;
        }

        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            properties[propertyName] = value.ToString();
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : value.ToString();
    }

    private static string NormalizePartOfSpeech(string pos)
    {
        return pos.ToLowerInvariant() switch
        {
            "adj" or "adjective" => "adjective",
            "adv" or "adverb" => "adverb",
            "article" or "det" or "determiner" => "article",
            "conj" or "conjunction" => "conjunction",
            "intj" or "interj" or "interjection" => "interjection",
            "noun" or "proper noun" => "noun",
            "num" or "numeral" or "number" => "numeral",
            "prep" or "preposition" => "preposition",
            "pron" or "pronoun" => "pronoun",
            "verb" => "verb",
            "" => string.Empty,
            _ => pos
        };
    }

    private static bool IsTemplateForm(string form)
    {
        return form.Equals("no-table-tags", StringComparison.OrdinalIgnoreCase)
            || form.StartsWith("de-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemplateTag(string tag)
    {
        return tag.Equals("table-tags", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("inflection-template", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string line)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(line));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void PrintDryRunEntry(DictionaryEntry entry, int count)
    {
        if (count > 5)
        {
            return;
        }

        Console.WriteLine($"{entry.Word} [{entry.PartOfSpeech}]");
        Console.WriteLine($"  description: {entry.Description}");
        Console.WriteLine($"  example: {entry.ExampleSentence}");
        Console.WriteLine($"  properties: {entry.Properties}");
        Console.WriteLine($"  variants: {entry.Variants[..Math.Min(entry.Variants.Length, 240)]}");
    }

    private static void PrintProgress(KaikkiImportResult result)
    {
        Console.WriteLine($"Read {result.LinesRead:n0}, parsed {result.Parsed:n0}, inserted {result.Inserted:n0}, skipped {result.Skipped:n0}");
    }
}

public sealed record KaikkiImportOptions(
    string Path,
    bool DryRun = false,
    bool ApplyMigrations = false,
    int BatchSize = 500,
    int? Limit = null,
    string? CheckpointPath = null,
    bool Resume = false);

public sealed class KaikkiImportResult
{
    public int LinesRead { get; set; }
    public int Parsed { get; set; }
    public int Inserted { get; set; }
    public int Skipped { get; set; }
}

public sealed class KaikkiImportCheckpoint
{
    public string SourcePath { get; set; } = string.Empty;
    public int LinesRead { get; set; }
    public int Parsed { get; set; }
    public int Inserted { get; set; }
    public int Skipped { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
