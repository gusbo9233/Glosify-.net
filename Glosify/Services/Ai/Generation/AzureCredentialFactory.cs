using Azure.Core;
using Azure.Identity;

namespace Glosify.Services.Ai.Generation;

/// <summary>
/// Creates the Azure credential still used by Blob Storage, Azure Speech,
/// Translator, and telemetry. OpenAI authentication uses OPENAI_SECRET_KEY.
/// </summary>
internal static class AzureCredentialFactory
{
    internal static TokenCredential Create(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (environment.IsDevelopment())
        {
            return new DefaultAzureCredential();
        }

        var clientId = configuration["AZURE_CLIENT_ID"];
        return string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(clientId));
    }
}
