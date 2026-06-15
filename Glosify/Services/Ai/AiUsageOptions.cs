namespace Glosify.Services;

public sealed class AiUsageOptions
{
    public int TrialGrantCredits { get; set; } = 25;
    public int CreditsPerThousandTokens { get; set; } = 1;
    public int AssistantOutputTokenReserve { get; set; } = 2048;
    public int RepairOutputTokenReserve { get; set; } = 1024;
    public int ImageExtractionOutputTokenReserve { get; set; } = 1024;

    public int GetOutputReserve(string feature)
    {
        return feature switch
        {
            AiUsageFeatures.Assistant => AssistantOutputTokenReserve,
            AiUsageFeatures.Repair => RepairOutputTokenReserve,
            AiUsageFeatures.ImageExtraction => ImageExtractionOutputTokenReserve,
            _ => AssistantOutputTokenReserve,
        };
    }
}

public static class AiUsageFeatures
{
    public const string Assistant = "assistant";
    public const string Repair = "repair";
    public const string ImageExtraction = "image_extraction";
}
