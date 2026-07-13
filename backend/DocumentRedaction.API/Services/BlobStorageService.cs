using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using System.Text;

namespace DocumentRedaction.API.Services;

public sealed class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _serviceClient;
    private readonly BlobContainerClient _unredactedContainer;
    private readonly BlobContainerClient _redactedContainer;
    private readonly string _unredactedContainerName;
    private readonly string _redactedContainerName;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(IConfiguration config, TokenCredential credential, ILogger<BlobStorageService> logger)
    {
        _logger = logger;

        var serviceUri = config["Azure:Storage:ServiceUri"];
        if (string.IsNullOrWhiteSpace(serviceUri))
            throw new InvalidOperationException(
                "Azure:Storage:ServiceUri is not configured. Set it to your blob endpoint, " +
                "e.g. https://<account>.blob.core.windows.net");

        _unredactedContainerName = config["Azure:Storage:UnredactedContainerName"]
            ?? config["Azure:Storage:ContainerName"]
            ?? "documents-unredacted";
        _redactedContainerName = config["Azure:Storage:RedactedContainerName"]
            ?? config["Azure:Storage:ContainerName"]
            ?? "documents-redacted";

        // Identity-based auth only (no account keys). The credential is supplied by DI
        // (DefaultAzureCredential: Managed Identity in Azure, az login locally) and shared
        // with the Language service.
        _serviceClient = new BlobServiceClient(new Uri(serviceUri), credential);
        _unredactedContainer = _serviceClient.GetBlobContainerClient(_unredactedContainerName);
        _redactedContainer = _serviceClient.GetBlobContainerClient(_redactedContainerName);
    }

    public async Task<string> UploadAsync(
        Stream content,
        string blobName,
        string contentType,
        CancellationToken ct = default)
    {
        var blob = _unredactedContainer.GetBlobClient(blobName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        };

        _logger.LogInformation("Uploading unredacted blob {BlobName}", blobName);
        await blob.UploadAsync(content, options, ct);

        return blob.Uri.ToString();
    }

    public async Task<string> UploadTextAsync(string text, string blobName, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var blob = _redactedContainer.GetBlobClient(blobName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain" }
        };

        _logger.LogInformation("Uploading redacted blob {BlobName}", blobName);
        await blob.UploadAsync(stream, options, ct);

        return blob.Uri.ToString();
    }

    public async Task<string> GenerateBlobSasUrlAsync(
        string blobName,
        BlobSasPermissions permissions,
        TimeSpan validity,
        CancellationToken ct = default)
    {
        var userDelegationKey = await _serviceClient.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.Add(validity),
            ct);

        var sasBuilder = new BlobSasBuilder(permissions, DateTimeOffset.UtcNow.Add(validity))
        {
            BlobContainerName = _unredactedContainerName,
            BlobName = blobName,
            Resource = "b"
        };

        var sasToken = sasBuilder.ToSasQueryParameters(userDelegationKey, _serviceClient.AccountName).ToString();
        var blobUri = _unredactedContainer.GetBlobClient(blobName).Uri;
        return $"{blobUri}?{sasToken}";
    }

    public async Task<string> GenerateContainerSasUrlAsync(
        BlobContainerSasPermissions permissions,
        TimeSpan validity,
        CancellationToken ct = default)
    {
        var userDelegationKey = await _serviceClient.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.Add(validity),
            ct);

        var sasBuilder = new BlobSasBuilder(permissions, DateTimeOffset.UtcNow.Add(validity))
        {
            BlobContainerName = _redactedContainerName,
            Resource = "c"
        };

        var sasToken = sasBuilder.ToSasQueryParameters(userDelegationKey, _serviceClient.AccountName).ToString();
        return $"{_redactedContainer.Uri}?{sasToken}";
    }

    public async Task<string> DownloadTextBlobAsync(string blobUrl, CancellationToken ct = default)
    {
        // Strip query string (SAS token) and extract the blob path within the container.
        var uri = new Uri(blobUrl);
        // AbsolutePath looks like /<containerName>/<blobPath>
        var absolutePath = uri.AbsolutePath.TrimStart('/');
        BlobContainerClient? containerClient = null;
        string? blobName = null;

        if (absolutePath.StartsWith(_unredactedContainerName + "/", StringComparison.OrdinalIgnoreCase))
        {
            containerClient = _unredactedContainer;
            blobName = absolutePath[(_unredactedContainerName.Length + 1)..];
        }
        else if (absolutePath.StartsWith(_redactedContainerName + "/", StringComparison.OrdinalIgnoreCase))
        {
            containerClient = _redactedContainer;
            blobName = absolutePath[(_redactedContainerName.Length + 1)..];
        }

        if (containerClient is null || string.IsNullOrWhiteSpace(blobName))
            throw new InvalidOperationException(
                $"Blob URL '{blobUrl}' does not belong to the configured storage containers.");

        _logger.LogInformation("Downloading blob {BlobName}", blobName);
        var blobClient = containerClient.GetBlobClient(blobName);
        var response = await blobClient.DownloadContentAsync(ct);
        return response.Value.Content.ToString();
    }

    public async Task<BlobDownload> DownloadDocumentAsync(string blobUrl, CancellationToken ct = default)
    {
        var uri = new Uri(blobUrl);
        var absolutePath = uri.AbsolutePath.TrimStart('/');
        BlobContainerClient? containerClient = null;
        string? blobName = null;

        if (absolutePath.StartsWith(_unredactedContainerName + "/", StringComparison.OrdinalIgnoreCase))
        {
            containerClient = _unredactedContainer;
            blobName = absolutePath[(_unredactedContainerName.Length + 1)..];
        }
        else if (absolutePath.StartsWith(_redactedContainerName + "/", StringComparison.OrdinalIgnoreCase))
        {
            containerClient = _redactedContainer;
            blobName = absolutePath[(_redactedContainerName.Length + 1)..];
        }

        if (containerClient is null || string.IsNullOrWhiteSpace(blobName))
            throw new InvalidOperationException(
                $"Blob URL '{blobUrl}' does not belong to the configured storage containers.");

        // Azure URL-encodes path segments; decode so the blob name matches storage.
        blobName = Uri.UnescapeDataString(blobName);

        _logger.LogInformation("Downloading document blob {BlobName}", blobName);
        var blobClient = containerClient.GetBlobClient(blobName);
        var response = await blobClient.DownloadContentAsync(ct);
        var contentType = response.Value.Details.ContentType ?? "application/octet-stream";
        return new BlobDownload(response.Value.Content, contentType);
    }

    public async Task<string> UploadToRedactedAsync(
        BinaryData content, string blobName, string contentType, CancellationToken ct = default)
    {
        var blob = _redactedContainer.GetBlobClient(blobName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        };

        _logger.LogInformation("Uploading redacted document blob {BlobName}", blobName);
        await blob.UploadAsync(content.ToStream(), options, ct);
        return blob.Uri.ToString();
    }

    public Task<BlobStream?> OpenOriginalDocumentAsync(string jobId, CancellationToken ct = default) =>
        OpenByPrefixAsync(_unredactedContainer, $"original/{jobId}", ct);

    public Task<BlobStream?> OpenRedactedDocumentAsync(string jobId, CancellationToken ct = default) =>
        OpenByPrefixAsync(_redactedContainer, $"redacted/{jobId}", ct);

    public async Task UploadStateAsync(string jobId, string json, CancellationToken ct = default)
    {
        var blob = _unredactedContainer.GetBlobClient($"state/{jobId}.json");
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await blob.UploadAsync(stream, options, ct);
    }

    public async Task<string?> DownloadStateAsync(string jobId, CancellationToken ct = default)
    {
        var blob = _unredactedContainer.GetBlobClient($"state/{jobId}.json");
        if (!await blob.ExistsAsync(ct))
            return null;

        var response = await blob.DownloadContentAsync(ct);
        return response.Value.Content.ToString();
    }

    public async Task<BlobDownload?> DownloadOriginalAsync(string jobId, CancellationToken ct = default)
    {
        await foreach (var item in _unredactedContainer.GetBlobsAsync(
            traits: BlobTraits.None, states: BlobStates.None, prefix: $"original/{jobId}", cancellationToken: ct))
        {
            var blobClient = _unredactedContainer.GetBlobClient(item.Name);
            var response = await blobClient.DownloadContentAsync(ct);
            var contentType = response.Value.Details.ContentType ?? "application/octet-stream";
            return new BlobDownload(response.Value.Content, contentType);
        }

        return null;
    }

    private async Task<BlobStream?> OpenByPrefixAsync(
        BlobContainerClient container, string prefix, CancellationToken ct)
    {
        await foreach (var item in container.GetBlobsAsync(
            traits: BlobTraits.None, states: BlobStates.None, prefix: prefix, cancellationToken: ct))
        {
            var blobClient = container.GetBlobClient(item.Name);
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
            var contentType = response.Value.Details.ContentType ?? "application/octet-stream";
            return new BlobStream(response.Value.Content, contentType, item.Name);
        }

        return null;
    }

}
