using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Glosify.Services.Ai;

namespace Glosify.Services.Storage;

public sealed class AzureBlobBookFileStorage : IBookFileStorage
{
    private readonly BlobContainerClient _containerClient;
    private readonly IPaidServiceGate _paidServices;

    public AzureBlobBookFileStorage(
        GlosifyBlobServiceClient blobs,
        IPaidServiceGate paidServices)
    {
        _containerClient = blobs.GetRequiredDefaultContainer();
        _paidServices = paidServices;
    }

    public async Task<string> UploadAsync(
        Stream content,
        string blobName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await _paidServices.EnsureAvailableAsync(cancellationToken);
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

    /// <summary>
    /// Opens the blob as a seekable stream.
    /// </summary>
    /// <remarks>
    /// Seekability is what lets the book endpoints answer HTTP range requests:
    /// FileStreamResult drops enableRangeProcessing without a word when the
    /// stream cannot seek, and DownloadStreamingAsync hands back a forward-only
    /// network stream. Every request therefore sent the whole PDF — including
    /// the ones pdf.js makes to draw a 112px shelf thumbnail, which pulled 13 MB
    /// onto the library page. OpenReadAsync seeks by issuing its own range
    /// requests to blob storage, so only the bytes a reader asks for move.
    /// </remarks>
    public async Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        blobName = RequireBlobName(blobName);
        var blobClient = _containerClient.GetBlobClient(blobName);
        return await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
    }

    public async Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default)
    {
        blobName = RequireBlobName(blobName);
        var blobClient = _containerClient.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
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
