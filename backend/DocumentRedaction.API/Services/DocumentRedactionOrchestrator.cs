using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Orchestrates the full redaction pipeline:
///   1. Upload original document to Blob Storage
///   2. Extract text via Azure AI Document Intelligence
///   3. Detect and redact PII via Azure AI Language
///   4. Upload redacted text to Blob Storage
///   5. Return the consolidated <see cref="RedactionResponse"/>
/// </summary>
public sealed class DocumentRedactionOrchestrator : IDocumentRedactionOrchestrator
{
    // 50 MB upload limit
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/tiff",
        "image/bmp",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",       // .xlsx
        "application/vnd.openxmlformats-officedocument.presentationml.presentation", // .pptx
        "text/html"
    };

    private readonly IBlobStorageService _blobStorage;
    private readonly IDocumentExtractionService _extractor;
    private readonly IPiiRedactionService _piiRedactor;
    private readonly ILogger<DocumentRedactionOrchestrator> _logger;

    public DocumentRedactionOrchestrator(
        IBlobStorageService blobStorage,
        IDocumentExtractionService extractor,
        IPiiRedactionService piiRedactor,
        ILogger<DocumentRedactionOrchestrator> logger)
    {
        _blobStorage = blobStorage;
        _extractor = extractor;
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
        string originalBlobUrl;
        using (var originalStream = file.OpenReadStream())
        {
            var originalBlobName = $"original/{jobId}{extension}";
            originalBlobUrl = await _blobStorage.UploadAsync(
                originalStream, originalBlobName, file.ContentType, ct);
        }

        // --- Step 2: Extract text ---
        string extractedText;
        using (var extractStream = file.OpenReadStream())
        {
            extractedText = await _extractor.ExtractTextAsync(extractStream, file.ContentType, ct);
        }

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            _logger.LogWarning("Job {JobId}: Document Intelligence returned no text", jobId);
            extractedText = string.Empty;
        }

        // --- Step 3: Redact PII ---
        var (redactedText, entities) = string.IsNullOrWhiteSpace(extractedText)
            ? (string.Empty, (IReadOnlyList<RedactedEntity>)[])
            : await _piiRedactor.RedactAsync(extractedText, ct);

        // --- Step 4: Store redacted text ---
        var redactedBlobName = $"redacted/{jobId}.txt";
        var redactedBlobUrl = await _blobStorage.UploadTextAsync(redactedText, redactedBlobName, ct);

        _logger.LogInformation(
            "Job {JobId} complete. {EntityCount} PII entity/entities redacted.",
            jobId, entities.Count);

        return new RedactionResponse
        {
            JobId = jobId,
            OriginalBlobUrl = originalBlobUrl,
            RedactedBlobUrl = redactedBlobUrl,
            ExtractedText = extractedText,
            RedactedText = redactedText,
            RedactedEntities = entities,
            FileName = safeFileName,
            FileSizeBytes = file.Length,
            ProcessedAt = DateTimeOffset.UtcNow
        };
    }

    private static void ValidateFile(IFormFile file)
    {
        if (file is null || file.Length == 0)
            throw new ArgumentException("No file provided or file is empty.");

        if (file.Length > MaxFileSizeBytes)
            throw new ArgumentException($"File exceeds the maximum allowed size of 50 MB.");

        if (!SupportedContentTypes.Contains(file.ContentType))
            throw new ArgumentException(
                $"Unsupported file type '{file.ContentType}'. " +
                "Supported types: PDF, JPEG, PNG, TIFF, BMP, DOCX, XLSX, PPTX, HTML.");
    }
}
