using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
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
        TokenCredential credential,
        IHostEnvironment environment)
    {
        _options = options.Value;
        _serviceClient = CreateServiceClient(_options, credential, environment);
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
        TokenCredential credential,
        IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            var client = new BlobServiceClient(options.ConnectionString);
            RequireSecureEndpoint(client.Uri, environment);
            return client;
        }

        var serviceUri = !string.IsNullOrWhiteSpace(options.ServiceUri)
            ? options.ServiceUri
            : !string.IsNullOrWhiteSpace(options.AccountName)
                ? $"https://{options.AccountName}.blob.core.windows.net"
                : null;
        if (serviceUri is null)
        {
            return null;
        }

        var uri = new Uri(serviceUri, UriKind.Absolute);
        RequireSecureEndpoint(uri, environment);
        return new BlobServiceClient(uri, credential);
    }

    private static void RequireSecureEndpoint(Uri uri, IHostEnvironment environment)
    {
        if (uri.Scheme == Uri.UriSchemeHttps
            || (environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return;
        }

        throw new InvalidOperationException(
            "Blob storage must use HTTPS. HTTP is allowed only for a loopback emulator in Development.");
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
