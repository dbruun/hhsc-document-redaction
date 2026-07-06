using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace DocumentRedaction.API.Services;

public interface IBlobStorageService
{
    /// <summary>Uploads a file stream and returns the blob URL.</summary>
    Task<string> UploadAsync(Stream content, string blobName, string contentType, CancellationToken ct = default);

    /// <summary>Uploads UTF-8 text and returns the blob URL.</summary>
    Task<string> UploadTextAsync(string text, string blobName, CancellationToken ct = default);

    /// <summary>
    /// Generates a user-delegation SAS URL for a specific blob with the given permissions.
    /// </summary>
    Task<string> GenerateBlobSasUrlAsync(
        string blobName,
        BlobSasPermissions permissions,
        TimeSpan validity,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a user-delegation SAS URL for the container with the given permissions.
    /// </summary>
    Task<string> GenerateContainerSasUrlAsync(
        BlobContainerSasPermissions permissions,
        TimeSpan validity,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads the text content of a blob identified by its full URL.
    /// The blob must reside in the container this service manages.
    /// </summary>
    Task<string> DownloadTextBlobAsync(string blobUrl, CancellationToken ct = default);
}
