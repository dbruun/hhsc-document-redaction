using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;

namespace DocumentRedaction.API.Services;

public sealed class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(IConfiguration config, ILogger<BlobStorageService> logger)
    {
        _logger = logger;

        var connectionString = config["Azure:Storage:ConnectionString"]
            ?? throw new InvalidOperationException("Azure:Storage:ConnectionString is required.");
        var containerName = config["Azure:Storage:ContainerName"] ?? "documents";

        _container = new BlobContainerClient(connectionString, containerName);
    }

    public async Task<string> UploadAsync(
        Stream content,
        string blobName,
        string contentType,
        CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

        var blob = _container.GetBlobClient(blobName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        };

        _logger.LogInformation("Uploading blob {BlobName}", blobName);
        await blob.UploadAsync(content, options, ct);

        return blob.Uri.ToString();
    }

    public async Task<string> UploadTextAsync(string text, string blobName, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await UploadAsync(stream, blobName, "text/plain", ct);
    }
}
