using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Glosify.Services.Language;
using Glosify.Services.Speech;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Avatar;

public sealed record AvatarVoice(string Id, string Name, bool Native);

public interface IAvatarVoices
{
    Task<AvatarVoice> ResolveAsync(QuizLanguage language, CancellationToken ct);
}

// Match provider metadata, never saved names (a renamed voice may name the wrong language).
// Resolve before billing and pin the result for the entire conversation.
public sealed class AvatarVoices(HttpClient http, IMemoryCache cache, IOptions<SpeechOptions> speech,
    IOptions<AvatarOptions> options) : IAvatarVoices
{
    public async Task<AvatarVoice> ResolveAsync(QuizLanguage language, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var voices = await cache.GetOrCreateAsync("avatar:female-voices", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                var result = new List<ProviderVoice>();
                string? next = null;
                for (var page = 0; page < 20; page++)
                {
                    var url = "https://api.elevenlabs.io/v2/voices?gender=female&page_size=100&include_total_count=false&include_custom_rates=false&include_live_moderated=false";
                    if (next is not null) url += "&next_page_token=" + Uri.EscapeDataString(next);
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("xi-api-key", speech.Value.ApiKey);
                    using var response = await http.SendAsync(request, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    var data = await response.Content.ReadFromJsonAsync<VoicePage>(timeout.Token) ?? throw Unavailable();
                    if (data.Voices is null) throw Unavailable();
                    result.AddRange(data.Voices.Where(v => !string.IsNullOrWhiteSpace(v.Id)
                        && Label(v, "gender") == "female" && Label(v, "age") is not ("child" or "teen")));
                    if (!data.HasMore) return result;
                    if (string.IsNullOrEmpty(data.Next) || data.Next == next) throw Unavailable();
                    next = data.Next;
                }
                throw Unavailable(); // Do not pick from a truncated catalogue.
            }) ?? throw Unavailable();

            var code = SpeechLanguage(language);
            var selected = voices.OrderBy(v => Normalize(Label(v, "language")) == code ? 0
                    : v.Languages?.Any(l => Normalize(l.Language) == code) == true ? 1 : 2)
                .ThenBy(v => Label(v, "use_case") == "conversational" ? 0 : 1)
                .ThenBy(v => v.Id == "EXAVITQu4vr4xnSDxMaL" ? 0 : 1)
                .ThenBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();
            if (options.Value.VoiceIds.TryGetValue(language.Code, out var configured))
                selected = voices.SingleOrDefault(v => v.Id == configured)
                    ?? throw new AvatarException(503, "The configured avatar voice is unavailable. Ask an administrator to check the voice for this language.");
            if (selected is null) throw Unavailable();
            return new(selected.Id, selected.Name, Normalize(Label(selected, "language")) == code);
        }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (System.Text.Json.JsonException) { throw Unavailable(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
    }

    internal static string SpeechLanguage(QuizLanguage language) => Normalize(language.Code);
    private static string Normalize(string code) => code.Split('-')[0].ToLowerInvariant() switch { "nb" or "nn" => "no", var value => value };
    private static string Label(ProviderVoice voice, string name) => voice.Labels?.GetValueOrDefault(name, "")?.ToLowerInvariant() ?? "";
    private static AvatarException Unavailable() => new(503, "A suitable avatar voice is temporarily unavailable. Please retry.");

    private sealed record VoicePage(
        [property: JsonPropertyName("voices")] List<ProviderVoice> Voices,
        [property: JsonPropertyName("has_more")] bool HasMore,
        [property: JsonPropertyName("next_page_token")] string? Next);
    private sealed record ProviderVoice(
        [property: JsonPropertyName("voice_id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("labels")] Dictionary<string, string> Labels)
    {
        [JsonPropertyName("verified_languages")] public List<VerifiedLanguage> Languages { get; init; } = [];
    }
    private sealed record VerifiedLanguage([property: JsonPropertyName("language")] string Language);
}
