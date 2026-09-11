using System.Diagnostics;
using System.Text.Json;

namespace Glosify.Services.Books;

public sealed class IsolatedPdfTextExtractionService : IPdfTextExtractionService
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    public async Task<IReadOnlyList<ExtractedPdfPage>> ExtractPagesAsync(Stream pdf, CancellationToken cancellationToken = default)
    {
        if (!await Slots.WaitAsync(0, cancellationToken)) throw new ArgumentException("PDF processing is busy. Please try again.");
        var folder = Path.Combine(Path.GetTempPath(), "glosify-pdf-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(folder, "input.pdf");
        var output = Path.Combine(folder, "pages.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Process? process = null;
        try
        {
            Directory.CreateDirectory(folder);
            await using (var file = File.Create(input)) await pdf.CopyToAsync(file, timeout.Token);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("PDF worker executable is unavailable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(IsolatedPdfTextExtractionService).Assembly.Location);
            start.ArgumentList.Add("--extract-pdf"); start.ArgumentList.Add(input); start.ArgumentList.Add(output);
            // The parser has no need for provider keys, connection strings or host startup.
            start.Environment.Clear();
            start.Environment["DOTNET_GCHeapHardLimit"] = "10000000"; // 256 MiB, hexadecimal runtime setting.
            process = Process.Start(start) ?? throw new InvalidOperationException("PDF worker could not start.");
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                process.Refresh();
                if (process.WorkingSet64 > 512L * 1024 * 1024 || File.Exists(output) && new FileInfo(output).Length > 64L * 1024 * 1024)
                    throw new ArgumentException("PDF processing exceeded its resource limit.");
                await Task.Delay(100, timeout.Token);
            }
            if (process.ExitCode != 0 || !File.Exists(output)) throw new ArgumentException("The PDF is invalid, encrypted, or exceeds its processing limits.");
            if (new FileInfo(output).Length > 64L * 1024 * 1024) throw new ArgumentException("PDF text exceeds its limit.");
            await using var result = File.OpenRead(output);
            return await JsonSerializer.DeserializeAsync<List<ExtractedPdfPage>>(result, cancellationToken: timeout.Token)
                ?? throw new ArgumentException("The PDF could not be read.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ArgumentException("PDF processing exceeded 30 seconds."); }
        finally
        {
            try
            {
                if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); process.Dispose(); }
            }
            finally
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
                finally { Slots.Release(); }
            }
        }
    }

    public static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length != 3) return 1;
        try
        {
            await using var input = File.OpenRead(args[1]);
            if (input.Length > 25L * 1024 * 1024) return 1;
            var pages = await new PdfPigTextExtractionService().ExtractPagesAsync(input);
            await using var output = File.Create(args[2]);
            await JsonSerializer.SerializeAsync(output, pages);
            return 0;
        }
        catch { return 1; }
    }
}
