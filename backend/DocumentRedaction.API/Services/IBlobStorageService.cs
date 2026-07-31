using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace DocumentRedaction.API.Services;

public interface IBlobStorageService
{
    /// <summary>Uploads a file stream and returns the blob URL.</summary>
    Task<string> UploadAsync(Stream content, string blobName, string contentType, CancellationToken ct = default);

    /// <summary>Uploads UTF-8 text and returns the blob URL.</summary>
    Task<string> UploadTextAsync(string text, string blobName, CancellationToken ct = default);

    /// <summary>Generates a user-delegation SAS URL for a specific blob with the given permissions.</summary>
    Task<string> GenerateBlobSasUrlAsync(
        string blobName,
        BlobSasPermissions permissions,
        TimeSpan validity,
        CancellationToken ct = default);

    /// <summary>Generates a user-delegation SAS URL for the container with the given permissions.</summary>
    Task<string> GenerateContainerSasUrlAsync(
        BlobContainerSasPermissions permissions,
        TimeSpan validity,
        CancellationToken ct = default);

    /// <summary>Downloads the text content of a blob identified by its full URL.</summary>
    Task<string> DownloadTextBlobAsync(string blobUrl, CancellationToken ct = default);

    /// <summary>Downloads the raw bytes and content type of a blob identified by its full URL.</summary>
    Task<BlobDownload> DownloadDocumentAsync(string blobUrl, CancellationToken ct = default);

    /// <summary>Uploads raw bytes to the redacted container and returns the blob URL.</summary>
    Task<string> UploadToRedactedAsync(
        BinaryData content, string blobName, string contentType, CancellationToken ct = default);

    /// <summary>Opens the original uploaded document for the given job id, or null if missing.</summary>
    Task<BlobStream?> OpenOriginalDocumentAsync(string jobId, CancellationToken ct = default);

    /// <summary>Opens the redacted document for the given job id, or null if missing.</summary>
    Task<BlobStream?> OpenRedactedDocumentAsync(string jobId, CancellationToken ct = default);

    /// <summary>Persists the JSON job state (detection result) for a job.</summary>
    Task UploadStateAsync(string jobId, string json, CancellationToken ct = default);

    /// <summary>Reads the JSON job state for a job, or null if not found.</summary>
    Task<string?> DownloadStateAsync(string jobId, CancellationToken ct = default);

    /// <summary>Reads the original uploaded document bytes and content type for a job, or null.</summary>
    Task<BlobDownload?> DownloadOriginalAsync(string jobId, CancellationToken ct = default);
}

/// <summary>Raw content and content type of a downloaded blob.</summary>
public record BlobDownload(BinaryData Content, string ContentType);

/// <summary>An open blob stream with its content type and blob name.</summary>
public record BlobStream(Stream Content, string ContentType, string BlobName);
