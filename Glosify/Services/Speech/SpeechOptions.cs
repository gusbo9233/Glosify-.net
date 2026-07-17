namespace Glosify.Services.Speech;

public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    // Endpoint and ResourceId enable keyless Microsoft Entra authentication.
    // Endpoint must be the Speech resource's custom-domain endpoint.
    public string Endpoint { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;

    // Region and Key remain available for local or transitional key-based setups.
    public string Key { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string BlobContainer { get; set; } = "tts-cache";
    public int MaxTextLength { get; set; } = 200;
}
