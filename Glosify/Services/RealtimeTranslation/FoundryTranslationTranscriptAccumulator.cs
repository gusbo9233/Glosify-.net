using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glosify.Models.Entities;

namespace Glosify.Services.RealtimeTranslation;

internal sealed class FoundryTranslationTranscriptAccumulator
{
    // How long a buffer may sit untouched before it is stored without a closing
    // event. The translate deployment is not guaranteed to end a caption with a
    // ".done", and captions that only ever arrive as deltas would otherwise never
    // be saved at all.
    internal static readonly TimeSpan IdleFlush = TimeSpan.FromSeconds(4);

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

    private readonly Dictionary<string, Buffer> _buffers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedEventTypes = new(StringComparer.Ordinal);
    private int _sequence;

    /// <summary>
    /// Distinct translation event types seen, each tagged with the id field that
    /// grouped it. Event types only — never caption text.
    /// </summary>
    public IReadOnlyCollection<string> ObservedEventTypes => _observedEventTypes;

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

            var (key, keySource) = BuildKey(root);
            _observedEventTypes.Add($"{type}[{keySource}]");
            if (DeltaTypes.Contains(type))
            {
                var delta = GetString(root, "delta") ?? GetString(root, "text");
                if (!string.IsNullOrEmpty(delta))
                {
                    if (!_buffers.TryGetValue(key, out var buffer))
                    {
                        buffer = new Buffer();
                        _buffers[key] = buffer;
                    }
                    buffer.Text.Append(delta);
                    buffer.LastAppendedAt = capturedAt;
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
                finalText = completedBuffer.Text.ToString();
            }
            else
            {
                _buffers.Remove(key);
            }
            if (string.IsNullOrWhiteSpace(finalText))
            {
                return null;
            }

            return Build(key, finalText, capturedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores buffers that have gone quiet for longer than <see cref="IdleFlush"/>,
    /// covering captions the provider never closes with a final event.
    /// </summary>
    public List<CapturedTranslationSegment> FlushIdle(DateTimeOffset now) =>
        Drain(key => now - _buffers[key].LastAppendedAt >= IdleFlush, now);

    /// <summary>
    /// Stores every remaining buffer, for use when the relay is shutting down.
    /// </summary>
    public List<CapturedTranslationSegment> FlushAll(DateTimeOffset now) =>
        Drain(_ => true, now);

    private List<CapturedTranslationSegment> Drain(Func<string, bool> shouldFlush, DateTimeOffset now)
    {
        var flushed = new List<CapturedTranslationSegment>();
        if (_buffers.Count == 0)
        {
            return flushed;
        }
        foreach (var key in _buffers.Keys.ToList())
        {
            if (!shouldFlush(key))
            {
                continue;
            }
            var text = _buffers[key].Text.ToString();
            _buffers.Remove(key);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            _completed.Add(key);
            flushed.Add(Build(key, text, now));
        }
        return flushed;
    }

    private CapturedTranslationSegment Build(string key, string text, DateTimeOffset capturedAt)
    {
        var sequence = ++_sequence;
        // A grouped key repeats across flushes, so the stored key carries the
        // sequence too; otherwise the second caption of a session would look like a
        // duplicate of the first and be dropped on write.
        return new CapturedTranslationSegment(
            sequence,
            BoundKey($"{key}#{sequence}"),
            text.Trim(),
            capturedAt,
            RealtimeTranslationTranscriptStreams.Translation);
    }

    private static (string Key, string Source) BuildKey(JsonElement root)
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
            return (BoundKey($"response:{responseId}:{outputIndex}:{contentIndex}"), "response_id");
        }
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            return (BoundKey($"item:{itemId}:{contentIndex}"), "item_id");
        }
        // Deltas carrying neither id would otherwise land in a buffer of their own
        // per event, leaving one segment per fragment. Group them into a single
        // running caption instead, closed by the idle flush.
        return (BoundKey($"stream:{contentIndex}"), "none");
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

    private sealed class Buffer
    {
        public StringBuilder Text { get; } = new();
        public DateTimeOffset LastAppendedAt { get; set; }
    }
}
