using System.Text.Json;

namespace Glosify.Services.RealtimeTranslation;

internal static class FoundryTranslationProtocol
{
    internal const string BrowserProtocol = "glosify-realtime";
    internal const string RelayTokenProtocolPrefix = "relay-token.";
    internal const int MaximumBrowserMessageBytes = 64 * 1024;
    internal const int MaximumFoundryMessageBytes = 256 * 1024;

    internal static Uri BuildWebSocketUri(RealtimeTranslationOptions options)
    {
        if (!RealtimeTranslationOptionsValidator.TryValidateFoundryEndpoint(
                options.FoundryEndpoint,
                out var endpoint))
        {
            throw new RealtimeTranslationUnavailableException(
                "Live subtitles are not configured on this Glosify deployment.");
        }

        var builder = new UriBuilder(endpoint)
        {
            Scheme = "wss",
            Port = -1,
            Path = "openai/v1/realtime/translations",
            Query = "model=" + Uri.EscapeDataString(options.Deployment),
        };
        return builder.Uri;
    }

    internal static byte[] CreateSessionUpdate(string targetLanguage) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "session.update",
            session = new
            {
                audio = new
                {
                    output = new { language = targetLanguage },
                },
            },
        });

    internal static bool IsAllowedBrowserMessage(ReadOnlySpan<byte> payload) =>
        TryDecodeBrowserAudio(payload, out _);

    internal static bool TryDecodeBrowserAudio(
        ReadOnlySpan<byte> payload,
        out byte[] audioBytes)
    {
        audioBytes = [];
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "session.input_audio_buffer.append"
                || !root.TryGetProperty("audio", out var audio)
                || audio.ValueKind != JsonValueKind.String
                || audio.GetString() is not { Length: > 0 and <= 48_000 } audioText)
            {
                return false;
            }

            var decoded = GC.AllocateUninitializedArray<byte>((audioText.Length * 3 + 3) / 4);
            if (!Convert.TryFromBase64String(audioText, decoded, out var audioByteCount)
                || audioByteCount <= 0
                || audioByteCount % sizeof(short) != 0)
            {
                return false;
            }
            audioBytes = decoded.AsSpan(0, audioByteCount).ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool ShouldForwardFoundryMessage(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return false;
            }

            var type = typeElement.GetString();
            return !string.IsNullOrWhiteSpace(type)
                && !type.StartsWith("session.output_audio.", StringComparison.Ordinal)
                && !type.StartsWith("response.output_audio.", StringComparison.Ordinal)
                && !type.StartsWith("session.input_transcript.", StringComparison.Ordinal)
                && !type.StartsWith("conversation.item.input_audio_transcription.", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool HasType(ReadOnlySpan<byte> payload, string expectedType)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            return document.RootElement.TryGetProperty("type", out var typeElement)
                && typeElement.GetString() == expectedType;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
