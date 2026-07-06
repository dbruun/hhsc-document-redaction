using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using System.Text;

namespace DocumentRedaction.API.Services;

public sealed class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly StorageSharedKeyCredential _credential;
    private readonly string _containerName;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(IConfiguration config, ILogger<BlobStorageService> logger)
    {
        _logger = logger;

        var connectionString = config["Azure:Storage:ConnectionString"]
            ?? throw new InvalidOperationException("Azure:Storage:ConnectionString is required.");
        _containerName = config["Azure:Storage:ContainerName"] ?? "documents";

        _credential = ParseCredential(connectionString);
        _container = new BlobContainerClient(connectionString, _containerName);
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

    public string GenerateBlobSasUrl(string blobName, BlobSasPermissions permissions, TimeSpan validity)
    {
        var sasBuilder = new BlobSasBuilder(permissions, DateTimeOffset.UtcNow.Add(validity))
        {
            BlobContainerName = _containerName,
            BlobName = blobName,
            Resource = "b"
        };

        var sasToken = sasBuilder.ToSasQueryParameters(_credential).ToString();
        var blobUri = _container.GetBlobClient(blobName).Uri;
        return $"{blobUri}?{sasToken}";
    }

    public string GenerateContainerSasUrl(BlobContainerSasPermissions permissions, TimeSpan validity)
    {
        var sasBuilder = new BlobSasBuilder(permissions, DateTimeOffset.UtcNow.Add(validity))
        {
            BlobContainerName = _containerName,
            Resource = "c"
        };

        var sasToken = sasBuilder.ToSasQueryParameters(_credential).ToString();
        return $"{_container.Uri}?{sasToken}";
    }

    public async Task<string> DownloadTextBlobAsync(string blobUrl, CancellationToken ct = default)
    {
        // Strip query string (SAS token) and extract the blob path within the container.
        var uri = new Uri(blobUrl);
        // AbsolutePath looks like /<containerName>/<blobPath>
        var absolutePath = uri.AbsolutePath.TrimStart('/');
        var containerPrefix = _containerName + "/";

        if (!absolutePath.StartsWith(containerPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Blob URL '{blobUrl}' does not belong to container '{_containerName}'.");

        var blobName = absolutePath[containerPrefix.Length..];

        _logger.LogInformation("Downloading blob {BlobName}", blobName);
        var blobClient = _container.GetBlobClient(blobName);
        var response = await blobClient.DownloadContentAsync(ct);
        return response.Value.Content.ToString();
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private static StorageSharedKeyCredential ParseCredential(string connectionString)
    {
        var accountName = GetConnectionStringValue(connectionString, "AccountName");
        var accountKey = GetConnectionStringValue(connectionString, "AccountKey");
        return new StorageSharedKeyCredential(accountName, accountKey);
    }

    /// <summary>
    /// Extracts a named value from an Azure connection string of the form
    /// <c>Key=Value;Key=Value;…</c>.  Values may themselves contain <c>=</c>
    /// (e.g. base-64 storage keys).
    /// </summary>
    private static string GetConnectionStringValue(string connectionString, string key)
    {
        foreach (var segment in connectionString.Split(';'))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0) continue;

            var segmentKey = segment[..separatorIndex].Trim();
            if (string.Equals(segmentKey, key, StringComparison.OrdinalIgnoreCase))
                return segment[(separatorIndex + 1)..];
        }

        throw new InvalidOperationException(
            $"The storage connection string does not contain '{key}'.");
    }
}
