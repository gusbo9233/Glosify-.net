using Azure.Storage.Blobs.Models;

namespace Glosify.Services.Storage;

public interface IBookFileStorage
{
    Task<string> UploadAsync(
        Stream content,
        string blobName,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string blobName, CancellationToken cancellationToken = default);

    Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default);

    Task<BlobProperties> GetPropertiesAsync(string blobName, CancellationToken cancellationToken = default);
}
