using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glosify.Services.RealtimeTranslation;

internal sealed class FoundrySourceTranscriptAccumulator
{
    private static readonly HashSet<string> DeltaTypes = new(StringComparer.Ordinal)
    {
        "conversation.item.input_audio_transcription.delta",
        "response.text.delta",
    };

    private static readonly HashSet<string> FinalTypes = new(StringComparer.Ordinal)
    {
        "conversation.item.input_audio_transcription.completed",
        "response.text.done",
    };

    private readonly Dictionary<string, StringBuilder> _buffers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private int _sequence;

    public CapturedTranslationSegment? Apply(ReadOnlySpan<byte> payload, DateTimeOffset capturedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            var type = GetString(root, "type");
            if (type is null || (!DeltaTypes.Contains(type) && !FinalTypes.Contains(type)))
            {
                return null;
            }

            var key = BuildKey(root);
            if (DeltaTypes.Contains(type))
            {
                var delta = GetString(root, "delta") ?? GetString(root, "text");
                if (!string.IsNullOrEmpty(delta))
                {
                    if (!_buffers.TryGetValue(key, out var buffer))
                    {
                        buffer = new StringBuilder();
                        _buffers[key] = buffer;
                    }
                    buffer.Append(delta);
                }
                return null;
            }

            if (!_completed.Add(key))
            {
                return null;
            }
            var finalText = GetString(root, "text") ?? GetString(root, "transcript");
            if (string.IsNullOrWhiteSpace(finalText) && _buffers.Remove(key, out var completedBuffer))
            {
                finalText = completedBuffer.ToString();
            }
            else
            {
                _buffers.Remove(key);
            }
            if (string.IsNullOrWhiteSpace(finalText))
            {
                return null;
            }

            return new CapturedTranslationSegment(
                ++_sequence,
                key,
                finalText.Trim(),
                capturedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildKey(JsonElement root)
    {
        var itemId = GetString(root, "item_id");
        var responseId = GetString(root, "response_id");
        var eventId = GetString(root, "event_id");
        var raw = !string.IsNullOrWhiteSpace(itemId)
            ? $"source:item:{itemId}"
            : !string.IsNullOrWhiteSpace(responseId)
                ? $"source:response:{responseId}"
                : $"source:event:{eventId ?? Guid.NewGuid().ToString("N")}";
        if (raw.Length <= 256)
        {
            return raw;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return raw[..191] + ":" + hash;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
