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

        // Reconstruct the original extracted text from the redacted text + entity offsets.
        // The 'characterMask' policy preserves span lengths so offsets are identical in
        // both original and redacted text.
        var extractedText = ReconstructOriginalText(piiResult.RedactedText, piiResult.Entities);

        // --- Step 3: Store redacted text ---
        var redactedBlobName = $"redacted/{jobId}.txt";
        var redactedBlobUrl = await _blobStorage.UploadTextAsync(piiResult.RedactedText, redactedBlobName, ct);

        _logger.LogInformation(
            "Job {JobId} complete. {EntityCount} PII entity/entities redacted.",
            jobId, piiResult.Entities.Count);

        return new RedactionResponse
        {
            JobId = jobId,
            OriginalBlobUrl = originalBlobUrl,
            RedactedBlobUrl = redactedBlobUrl,
            ExtractedText = extractedText,
            RedactedText = piiResult.RedactedText,
            RedactedEntities = piiResult.Entities,
            FileName = safeFileName,
            FileSizeBytes = file.Length,
            ProcessedAt = DateTimeOffset.UtcNow
        };
    }

    // ─── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Reverses the character-mask redaction to recover the original plain text.
    /// Each entity carries the original PII text and the offset at which it
    /// appeared; since the mask preserves span length the offset is identical
    /// in both the original and the redacted string.
    /// </summary>
    private static string ReconstructOriginalText(
        string redactedText,
        IReadOnlyList<RedactedEntity> entities)
    {
        if (entities.Count == 0 || string.IsNullOrEmpty(redactedText))
            return redactedText;

        var chars = redactedText.ToCharArray();

        foreach (var entity in entities)
        {
            // entity.Length is the character span reported by the Language service (in the
            // original document text).  entity.Text.Length is the length of the C# string
            // returned by the API.  They should always be equal for ASCII/BMP content, but
            // can differ when surrogate pairs or combining characters are involved because the
            // service may measure offsets in UTF-16 code units while the .Text value is a
            // regular .NET string.  Taking the minimum is a safe defensive measure.
            var copyLen = Math.Min(entity.Text.Length, entity.Length);
            if (entity.Offset < 0 || entity.Offset + copyLen > chars.Length)
                continue;

            entity.Text.CopyTo(0, chars, entity.Offset, copyLen);
        }

        return new string(chars);
    }

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
