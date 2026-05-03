using Microsoft.Extensions.DependencyInjection;

namespace Glosify.Data.Importing;

public static class KaikkiImportCommand
{
    private const string CommandName = "import-kaikki-german";

    public static bool IsRequested(string[] args)
    {
        return args.FirstOrDefault()?.Equals(CommandName, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var options = ParseOptions(args.Skip(1).ToArray());
        using var scope = services.CreateScope();
        var importer = new KaikkiGermanDictionaryImporter(scope.ServiceProvider.GetRequiredService<GlosifyContext>());
        var result = await importer.ImportAsync(options);
        Console.WriteLine($"Done. Read {result.LinesRead:n0}, parsed {result.Parsed:n0}, inserted {result.Inserted:n0}, skipped {result.Skipped:n0}.");
        return 0;
    }

    private static KaikkiImportOptions ParseOptions(string[] args)
    {
        var path = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "kaikki.org-dictionary-German.jsonl";
        if (!File.Exists(path) && File.Exists(Path.Combine("Glosify", path)))
        {
            path = Path.Combine("Glosify", path);
        }

        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        var migrate = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
        var resume = args.Contains("--resume", StringComparer.OrdinalIgnoreCase);
        var batchSize = ReadIntOption(args, "--batch-size") ?? 500;
        var limit = ReadIntOption(args, "--limit");
        var checkpointPath = ReadStringOption(args, "--checkpoint");

        return new KaikkiImportOptions(path, dryRun, migrate, batchSize, limit, checkpointPath, resume);
    }

    private static int? ReadIntOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var value))
            {
                return value;
            }

            var prefix = $"{name}=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i][prefix.Length..], out value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadStringOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            var prefix = $"{name}=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..];
            }
        }

        return null;
    }
}
