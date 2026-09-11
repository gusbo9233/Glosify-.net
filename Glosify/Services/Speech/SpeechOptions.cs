namespace Glosify.Services.Speech;

public sealed class SpeechOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultVoiceId { get; set; } = "JBFqnCBsd6RMkjVDRZzb";
    public List<string> AllowedVoiceIds { get; set; } = ["JBFqnCBsd6RMkjVDRZzb"];
    public long MemoryCacheBytes { get; set; } = 64L * 1024 * 1024;
    public const string SectionName = "Speech";

    // Endpoint and ResourceId enable keyless Microsoft Entra authentication.
    // Endpoint must be the Speech resource's custom-domain endpoint.
    public string Endpoint { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;

    // Region and Key remain available for local or transitional key-based setups.
    public string Key { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string BlobContainer { get; set; } = "tts-cache";
    // Customer pricing is explicit and independent of package or provider-budget changes.
    public decimal TextSekPerMillionCharacters { get; set; } = 1928.29m;
    public decimal SekPerCredit { get; set; } = 0.1058m;
    public decimal CalculateCredits(int characters) => Glosify.Services.Ai.CreditAmounts.RoundCharge(
        Math.Max(0, characters) * TextSekPerMillionCharacters / 1_000_000m / SekPerCredit);
    public int MaximumSegmentCredits => Glosify.Services.Ai.CreditAmounts.Display(CalculateCredits(MaxTextLength));
    // Retained only for the retired Azure service's compatibility tests.
    public int CreditsPerRequest { get; set; } = 1;
    public int MaxTextLength { get; set; } = 200;
    public SpeechHighDefinitionOptions HighDefinition { get; set; } = new();
}

public sealed class SpeechHighDefinitionOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
}
