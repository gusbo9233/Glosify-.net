using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glosify.Services.Ai;
using Glosify.Services.Language;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speech;

public sealed class SpeechBusyException() : Exception("Speech is busy. Please try again shortly.");

public sealed class SpeechAudioCache(IOptions<SpeechOptions> options) : IDisposable
{
    internal readonly MemoryCache Audio = new(new MemoryCacheOptions { SizeLimit = options.Value.MemoryCacheBytes });
    internal readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Pending = new();
    internal readonly SemaphoreSlim SynthesisSlots = new(4, 4);
    public void Dispose() { Audio.Dispose(); SynthesisSlots.Dispose(); }
}

public sealed class ElevenLabsTextToSpeechService(
    IHttpClientFactory clients, IOptions<SpeechOptions> options, IOptions<AiUsageOptions> usage,
    SpeechAudioCache cache, IServiceScopeFactory scopes) : ITextToSpeechService
{
    public const string Model = "eleven_v3";
    public const string ClientName = "ElevenLabsTts";
    private const int MaxAudioBytes = 2 * 1024 * 1024;
    // Explicit intersection with v3's published language list. Do not silently send
    // unsupported languages: ElevenLabs may ignore unsupported language_code values.
    private static readonly HashSet<string> Languages = new(
        "af ar hy as az be bn bs bg ca ceb ny hr cs da nl en et fil fi fr gl ka de el gu ha he hi hu is id ga it ja jv kn kk ky ko lv ln lt lb mk ms ml zh mr ne no ps fa pl pt pa ro ru sr sd sk sl so es sw sv ta te th tr uk ur vi cy".Split(' '));
    public bool IsConfigured => options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ApiKey)
        && usage.Value.MonthlyBudget.FindModelPrice(Model)?.TextSekPerMillionCharacters > 0;

    private static string? Language(string code)
    {
        // The retired voice map emits this browser locale for Cantonese. V3's
        // Mandarin support must not turn it into a paid Mandarin request.
        if (string.Equals(code, "zh-HK", StringComparison.OrdinalIgnoreCase)) return null;
        var language = QuizLanguageCatalog.LanguageLearning.FirstOrDefault(l =>
            string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)
            || string.Equals(l.Locale, code, StringComparison.OrdinalIgnoreCase)
            || string.Equals(l.Name, code, StringComparison.OrdinalIgnoreCase));
        var normalized = (language?.Code ?? code).Split('-')[0].ToLowerInvariant();
        if (normalized == "nb") normalized = "no";
        return Languages.Contains(normalized) ? normalized : null;
    }

    public async Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string languageCode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || Language(languageCode) is not { } lang) return [];
        var key = "voices:" + lang;
        if (cache.Audio.TryGetValue<SpeechVoice[]>(key, out var cached)) return cached!;
        var voices = new List<SpeechVoice>();
        foreach (var id in options.Value.AllowedVoiceIds.Distinct())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices/" + Uri.EscapeDataString(id));
            request.Headers.Add("xi-api-key", options.Value.ApiKey);
            using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 256 * 1024, cancellationToken));
            voices.Add(new(id, body.RootElement.GetProperty("name").GetString() ?? "Default voice", lang));
        }
        var result = voices.OrderByDescending(v => v.ShortName == options.Value.DefaultVoiceId).ToArray();
        cache.Audio.Set(key, result, new MemoryCacheEntryOptions { Size = 1024 + result.Length * 512, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
        return result;
    }

    public async Task<Stream> GetOrSynthesizeAsync(string text, string languageCode, bool preferHighDefinition = false,
        string? voicePreference = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("ElevenLabs speech is not configured.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > options.Value.MaxTextLength)
            throw new ArgumentException("Speech text exceeds the segment limit.");
        var lang = Language(languageCode) ?? throw new NotSupportedException("Choose browser speech for this language.");
        var voice = string.IsNullOrEmpty(voicePreference) ? options.Value.DefaultVoiceId : voicePreference;
        if (!options.Value.AllowedVoiceIds.Contains(voice, StringComparer.Ordinal)) throw new ArgumentException("Choose an available speech voice.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Model}|{voice}|{lang}|mp3_44100_128|auto|{text}")));
        if (cache.Audio.TryGetValue<byte[]>(key, out var audio)) return new MemoryStream(audio!, writable: false);
        var operation = cache.Pending.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(() => GenerateAsync(key, voice, lang, text)));
        var task = operation.Value;
        _ = task.ContinueWith(_ => cache.Pending.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, operation)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new MemoryStream(await task.WaitAsync(cancellationToken), writable: false);
    }

    private async Task<byte[]> GenerateAsync(string key, string voice, string lang, string text)
    {
        if (cache.Audio.TryGetValue<byte[]>(key, out var completed)) return completed!;
        if (!await cache.SynthesisSlots.WaitAsync(0))
        {
            Glosify.Services.Abuse.AbuseMetrics.RecordQuota("tts_concurrency", true);
            throw new SpeechBusyException();
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var scope = scopes.CreateAsyncScope();
            var budget = scope.ServiceProvider.GetRequiredService<ISpeechProviderBudget>();
            var charge = await budget.ReserveAsync(text.Length, timeout.Token);
            var dispatched = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.elevenlabs.io/v1/text-to-speech/" + Uri.EscapeDataString(voice) + "?output_format=mp3_44100_128");
                request.Headers.Add("xi-api-key", options.Value.ApiKey);
                request.Content = JsonContent.Create(new { text, model_id = Model, language_code = lang, apply_text_normalization = "auto" });
                dispatched = true;
                using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    // An explicit rejection did not synthesize audio. Unknown network
                    // outcomes keep their reservation as conservative provider spend.
                    if ((int)response.StatusCode < 500) dispatched = false;
                    response.EnsureSuccessStatusCode();
                }
                var bytes = await ReadBoundedAsync(response.Content, MaxAudioBytes, timeout.Token);
                if (bytes.Length == 0) throw new InvalidOperationException("Speech returned empty audio.");
                cache.Audio.Set(key, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
                return bytes;
            }
            finally { await budget.SettleAsync(charge, dispatched, CancellationToken.None); }
        }
        finally { cache.SynthesisSlots.Release(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidOperationException("Speech response exceeded its size limit.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (result.Length + read > limit) throw new InvalidOperationException("Speech response exceeded its size limit.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
