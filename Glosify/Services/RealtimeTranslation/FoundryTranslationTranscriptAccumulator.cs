using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Glosify.Models.Entities;

namespace Glosify.Services.RealtimeTranslation;

internal sealed class FoundryTranslationTranscriptAccumulator
{
    private static readonly HashSet<string> DeltaTypes = new(StringComparer.Ordinal)
    {
        "session.output_transcript.delta",
        "response.text.delta",
        "response.output_text.delta",
        "response.output_audio_transcript.delta",
    };

    private static readonly HashSet<string> FinalTypes = new(StringComparer.Ordinal)
    {
        "session.output_transcript.done",
        "response.text.done",
        "response.output_text.done",
        "response.output_audio_transcript.done",
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
                capturedAt,
                RealtimeTranslationTranscriptStreams.Translation);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildKey(JsonElement root)
    {
        var responseId = GetString(root, "response_id");
        var itemId = GetString(root, "item_id");
        var outputIndex = root.TryGetProperty("output_index", out var output) && output.TryGetInt32(out var outputValue)
            ? outputValue
            : 0;
        var contentIndex = root.TryGetProperty("content_index", out var index) && index.TryGetInt32(out var value)
            ? value
            : 0;
        if (!string.IsNullOrWhiteSpace(responseId))
        {
            return BoundKey($"response:{responseId}:{outputIndex}:{contentIndex}");
        }
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            return BoundKey($"item:{itemId}:{contentIndex}");
        }
        return BoundKey(GetString(root, "event_id") ?? $"anonymous:{Guid.NewGuid():N}");
    }

    private static string BoundKey(string key)
    {
        if (key.Length <= 256)
        {
            return key;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return key[..191] + ":" + hash;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
