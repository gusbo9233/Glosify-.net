using System.Diagnostics;
using System.Text;

namespace ProjectVault;

public sealed class Repository(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".cshtml", ".razor", ".js", ".jsx", ".ts", ".tsx", ".json", ".csproj", ".props", ".targets", ".sln", ".slnx", ".yml", ".yaml", ".bicep", ".tf", ".md", ".html" };
    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    public string Absolute(string path)
    {
        var absolute = Path.GetFullPath(Path.Combine(Root, path));
        if (!absolute.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Path is outside this repository.");
        var current = new FileInfo(absolute) as FileSystemInfo;
        while (current is not null && current.FullName != Root)
        {
            if (current.LinkTarget is not null) throw new ArgumentException("Symbolic links are not scanned or written.");
            current = current is FileInfo f ? f.Directory : (current as DirectoryInfo)?.Parent;
        }
        return absolute;
    }
    public async Task<string> Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await error;
        return process.ExitCode == 0 ? await output : "";
    }
    public async Task<SortedDictionary<string, string>> Inventory()
    {
        var files = await Git("ls-files", "-z", "--cached", "--others", "--exclude-standard");
        IEnumerable<string> candidates = files.Length > 0 ? files.Split('\0', StringSplitOptions.RemoveEmptyEntries) : Walk(Root).Select(Relative);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in candidates.Distinct().Order(StringComparer.Ordinal))
        {
            if (!Included(path)) continue;
            string absolute;
            try { absolute = Absolute(path); } catch (ArgumentException) { continue; }
            if (!File.Exists(absolute)) continue;
            result[path] = Format.Id(await File.ReadAllTextAsync(absolute));
        }
        return result;
    }
    public static bool Included(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(x => x is ".git" or "node_modules" or "bin" or "obj" or "dist" or "artifacts" or ".project-visualization" or ".idea" or ".playwright-cli" or "TestResults" or "coverage" or "vendor")) return false;
        if (path.Contains("tools/project-vault/", StringComparison.Ordinal) || path.EndsWith(".min.js") || path.EndsWith(".d.ts") || path.Contains("wwwroot/lib/")) return false;
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.StartsWith(".env") || name == "secrets.json" || name.EndsWith(".secrets.json")) return false;
        return name == ".editorconfig" || Extensions.Contains(Path.GetExtension(path));
    }
    static IEnumerable<string> Walk(string dir)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(path))
            {
                if (Path.GetFileName(path) is ".git" or "node_modules" or "bin" or "obj" or "dist" or ".project-visualization") continue;
                foreach (var child in Walk(path)) yield return child;
            }
            else yield return path;
        }
    }
    public async Task<(string Branch, string Revision, string Worktree)> Identity() =>
        ((await Git("branch", "--show-current")).Trim(), (await Git("rev-parse", "HEAD")).Trim(), Format.Id((await Git("rev-parse", "--absolute-git-dir")).Trim() + Root));
    public static string Fingerprint(SortedDictionary<string, string> files, string revision, string branch, string worktree) => Format.Id(Json(files) + revision + branch + worktree + Format.EngineVersion);
    static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value, Format.Json);
    public async Task<string> Read(string path) => await File.ReadAllTextAsync(Absolute(path));
}
