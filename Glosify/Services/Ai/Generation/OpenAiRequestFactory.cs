#pragma warning disable OPENAI001

using System.Security.Cryptography;
using System.Text;
using OpenAI.Responses;

namespace Glosify.Services.Ai.Generation;

internal static class OpenAiRequestFactory
{
    internal static CreateResponseOptions Create(string userId, int maxOutputTokens)
    {
        var request = new CreateResponseOptions
        {
            Model = OpenAiModels.Luna,
            StoredOutputEnabled = false,
            ParallelToolCallsEnabled = true,
            MaxOutputTokenCount = maxOutputTokens,
            SafetyIdentifier = CreateSafetyIdentifier(userId),
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium,
            },
        };
        request.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        return request;
    }

    internal static string CreateSafetyIdentifier(string userId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

#pragma warning restore OPENAI001
