using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Storage;

/// <summary>
/// Owns the process-wide Azure Storage pipeline and credential cache. Scoped storage
/// services can enforce request-bound policy without rebuilding Azure SDK clients.
/// </summary>
public sealed class GlosifyBlobServiceClient
{
    private readonly BlobStorageOptions _options;
    private readonly BlobServiceClient? _serviceClient;

    public GlosifyBlobServiceClient(
        IOptions<BlobStorageOptions> options,
        TokenCredential credential)
    {
        _options = options.Value;
        _serviceClient = CreateServiceClient(_options, credential);
    }

    public BlobContainerClient GetRequiredDefaultContainer()
    {
        if (_serviceClient is null)
        {
            throw new InvalidOperationException(
                "Blob storage is not configured. Set BlobStorage:AccountName, BlobStorage:ServiceUri, or BlobStorage:ConnectionString.");
        }

        return _serviceClient.GetBlobContainerClient(RequireContainerName(_options.ContainerName));
    }

    public BlobContainerClient? GetContainerOrDefault(string? containerName)
    {
        if (_serviceClient is null || string.IsNullOrWhiteSpace(containerName))
        {
            return null;
        }

        return _serviceClient.GetBlobContainerClient(containerName.Trim());
    }

    private static BlobServiceClient? CreateServiceClient(
        BlobStorageOptions options,
        TokenCredential credential)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobServiceClient(options.ConnectionString);
        }

        var serviceUri = !string.IsNullOrWhiteSpace(options.ServiceUri)
            ? options.ServiceUri
            : !string.IsNullOrWhiteSpace(options.AccountName)
                ? $"https://{options.AccountName}.blob.core.windows.net"
                : null;
        return serviceUri is null
            ? null
            : new BlobServiceClient(new Uri(serviceUri, UriKind.Absolute), credential);
    }

    private static string RequireContainerName(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException("BlobStorage:ContainerName must be configured.");
        }

        return containerName.Trim();
    }
}
