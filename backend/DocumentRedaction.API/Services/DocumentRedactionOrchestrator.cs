using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Orchestrates the full redaction pipeline:
///   1. Upload original document to Blob Storage
///   2. Detect and redact PII via the Azure AI Language native-document endpoint
///   3. Upload redacted text to Blob Storage
///   4. Return the consolidated <see cref="RedactionResponse"/>
/// </summary>
public sealed class DocumentRedactionOrchestrator : IDocumentRedactionOrchestrator
{
    // 50 MB upload limit
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx
        "text/plain"                                                                // .txt
    };

    private readonly IBlobStorageService _blobStorage;
    private readonly IDocumentPiiRedactionService _piiRedactor;
    private readonly ILogger<DocumentRedactionOrchestrator> _logger;

    public DocumentRedactionOrchestrator(
        IBlobStorageService blobStorage,
        IDocumentPiiRedactionService piiRedactor,
        ILogger<DocumentRedactionOrchestrator> logger)
    {
        _blobStorage = blobStorage;
        _piiRedactor = piiRedactor;
        _logger = logger;
    }

    public async Task<RedactionResponse> ProcessAsync(IFormFile file, CancellationToken ct = default)
    {
        ValidateFile(file);

        var jobId = Guid.NewGuid().ToString("N");
        var safeFileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(safeFileName);

        _logger.LogInformation(
            "Starting redaction job {JobId} for file '{FileName}' ({Bytes} bytes)",
            jobId, safeFileName, file.Length);

        // --- Step 1: Store original in blob storage ---
        var originalBlobName = $"original/{jobId}{extension}";
        string originalBlobUrl;
        using (var originalStream = file.OpenReadStream())
        {
            originalBlobUrl = await _blobStorage.UploadAsync(
                originalStream, originalBlobName, file.ContentType, ct);
        }

        // --- Step 2: Redact PII via Azure AI Language native-document endpoint ---
        var piiResult = await _piiRedactor.RedactDocumentAsync(originalBlobName, ct);

        // --- Step 3: Copy the Azure-generated redacted document to a deterministic
        //             name so it can be streamed back to the client by job id. ---
        var redactedDocument = await _blobStorage.DownloadDocumentAsync(piiResult.RedactedDocumentUrl, ct);
        var redactedBlobName = $"redacted/{jobId}{extension}";
        var redactedBlobUrl = await _blobStorage.UploadToRedactedAsync(
            redactedDocument.Content, redactedBlobName, redactedDocument.ContentType, ct);

        _logger.LogInformation(
            "Job {JobId} complete. {EntityCount} PII entity/entities redacted.",
            jobId, piiResult.Entities.Count);

        return new RedactionResponse
        {
            JobId = jobId,
            OriginalBlobUrl = originalBlobUrl,
            RedactedBlobUrl = redactedBlobUrl,
            ContentType = file.ContentType,
            ExtractedText = piiResult.ExtractedText,
            RedactedText = piiResult.RedactedText,
            RedactedEntities = piiResult.Entities,
            FileName = safeFileName,
            FileSizeBytes = file.Length,
            ProcessedAt = DateTimeOffset.UtcNow
        };
    }

    // ─── Private helpers ────────────────────────────────────────────────────────

    public Task<BlobStream?> OpenOriginalAsync(string jobId, CancellationToken ct = default) =>
        _blobStorage.OpenOriginalDocumentAsync(jobId, ct);

    public Task<BlobStream?> OpenRedactedAsync(string jobId, CancellationToken ct = default) =>
        _blobStorage.OpenRedactedDocumentAsync(jobId, ct);

    private static void ValidateFile(IFormFile file)
    {
        if (file is null || file.Length == 0)
            throw new ArgumentException("No file provided or file is empty.");

        if (file.Length > MaxFileSizeBytes)
            throw new ArgumentException("File exceeds the maximum allowed size of 50 MB.");

        if (!SupportedContentTypes.Contains(file.ContentType))
            throw new ArgumentException(
                $"Unsupported file type '{file.ContentType}'. " +
                "The Azure AI Language native-document endpoint supports PDF, DOCX, and TXT.");
    }
}
