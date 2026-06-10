using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Storage;

public sealed class AzureBlobBookFileStorage : IBookFileStorage
{
    private readonly BlobContainerClient _containerClient;

    public AzureBlobBookFileStorage(IOptions<BlobStorageOptions> options)
    {
        var storageOptions = options.Value;
        _containerClient = CreateContainerClient(storageOptions);
    }

    public async Task<string> UploadAsync(
        Stream content,
        string blobName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        blobName = RequireBlobName(blobName);

        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = _containerClient.GetBlobClient(blobName);
        var headers = new BlobHttpHeaders
        {
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType
        };

        await blobClient.UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = headers },
            cancellationToken);

        return blobClient.Name;
    }

    public async Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        blobName = RequireBlobName(blobName);
        var blobClient = _containerClient.GetBlobClient(blobName);
        var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    public async Task<bool> ExistsAsync(string blobName, CancellationToken cancellationToken = default)
    {
        blobName = RequireBlobName(blobName);
        var blobClient = _containerClient.GetBlobClient(blobName);
        return await blobClient.ExistsAsync(cancellationToken);
    }

    public async Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default)
    {
        blobName = RequireBlobName(blobName);
        var blobClient = _containerClient.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<BlobProperties> GetPropertiesAsync(string blobName, CancellationToken cancellationToken = default)
    {
        blobName = RequireBlobName(blobName);
        var blobClient = _containerClient.GetBlobClient(blobName);
        var response = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        return response.Value;
    }

    private static BlobContainerClient CreateContainerClient(BlobStorageOptions options)
    {
        var containerName = RequireContainerName(options.ContainerName);

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobContainerClient(options.ConnectionString, containerName);
        }

        var serviceUri = GetServiceUri(options);
        var serviceClient = new BlobServiceClient(
            new Uri(serviceUri, UriKind.Absolute),
            new DefaultAzureCredential());

        return serviceClient.GetBlobContainerClient(containerName);
    }

    private static string GetServiceUri(BlobStorageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServiceUri))
        {
            return options.ServiceUri;
        }

        if (!string.IsNullOrWhiteSpace(options.AccountName))
        {
            return $"https://{options.AccountName}.blob.core.windows.net";
        }

        throw new InvalidOperationException(
            "Blob storage is not configured. Set BlobStorage:AccountName, BlobStorage:ServiceUri, or BlobStorage:ConnectionString.");
    }

    private static string RequireContainerName(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException("BlobStorage:ContainerName must be configured.");
        }

        return containerName.Trim();
    }

    private static string RequireBlobName(string blobName)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new ArgumentException("Blob name must be provided.", nameof(blobName));
        }

        return blobName.Trim();
    }
}
