namespace Glosify.Services.Storage;

public sealed class BlobStorageOptions
{
    public string AccountName { get; set; } = string.Empty;
    public string ServiceUri { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "books";
}
