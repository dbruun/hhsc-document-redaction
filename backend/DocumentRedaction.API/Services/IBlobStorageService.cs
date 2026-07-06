namespace DocumentRedaction.API.Services;

public interface IBlobStorageService
{
    /// <summary>Uploads a file stream and returns the blob URL.</summary>
    Task<string> UploadAsync(Stream content, string blobName, string contentType, CancellationToken ct = default);

    /// <summary>Uploads UTF-8 text and returns the blob URL.</summary>
    Task<string> UploadTextAsync(string text, string blobName, CancellationToken ct = default);
}
