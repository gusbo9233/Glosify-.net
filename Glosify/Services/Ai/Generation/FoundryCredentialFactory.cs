using Azure.Core;
using Azure.Identity;

namespace Glosify.Services.Ai.Generation;

internal static class FoundryCredentialFactory
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
