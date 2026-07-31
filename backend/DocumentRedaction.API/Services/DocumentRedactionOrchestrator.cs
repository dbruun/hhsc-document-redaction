using DocumentRedaction.API.Models;
using System.Text.Json;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Coordinates the two-phase interactive redaction flow:
///   DETECT — store upload, extract text, detect every PII instance (with per-instance
///            boxes for PDFs), return them for review.
///   APPLY  — redact only the instances the reviewer selected, preserving the original
///            file format.
/// Detection is uniform across formats (Azure Language Text PII over the extracted text);
/// only extraction and the final burn are format-specific.
/// </summary>
public sealed class DocumentRedactionOrchestrator : IDocumentRedactionOrchestrator
{
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx
        "text/plain"                                                                // .txt
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEnumerable<IDocumentFormatProcessor> _processors;
    private readonly ITextPiiClient _pii;
    private readonly IBlobStorageService _blobStorage;
    private readonly ILogger<DocumentRedactionOrchestrator> _logger;

    public DocumentRedactionOrchestrator(
        IEnumerable<IDocumentFormatProcessor> processors,
        ITextPiiClient pii,
        IBlobStorageService blobStorage,
        ILogger<DocumentRedactionOrchestrator> logger)
    {
        _processors = processors;
        _pii = pii;
        _blobStorage = blobStorage;
        _logger = logger;
    }

    public async Task<DetectionResponse> DetectAsync(IFormFile file, CancellationToken ct = default)
    {
        ValidateFile(file);

        var jobId = Guid.NewGuid().ToString("N");
        var safeFileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(safeFileName);
        var contentType = file.ContentType;
        var processor = ResolveProcessor(contentType);

        _logger.LogInformation(
            "Detect job {JobId} for '{FileName}' ({Bytes} bytes, {ContentType})",
            jobId, safeFileName, file.Length, contentType);

        // Read the upload once and store the original.
        BinaryData content;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            content = BinaryData.FromBytes(ms.ToArray());
        }

        using (var uploadStream = content.ToStream())
        {
            await _blobStorage.UploadAsync(uploadStream, $"original/{jobId}{extension}", contentType, ct);
        }

        // Extract text (+ word boxes for PDF), then detect PII over that exact text.
        var extracted = await processor.ExtractAsync(content, ct);
        var rawEntities = await _pii.DetectAsync(extracted.Text, ct);

        var entities = BuildEntities(rawEntities, extracted);

        var response = new DetectionResponse
        {
            JobId = jobId,
            FileName = safeFileName,
            FileSizeBytes = file.Length,
            ContentType = contentType,
            ExtractedText = extracted.Text,
            PageCount = extracted.Pages.Count,
            Pages = extracted.Pages,
            Entities = entities,
            ProcessedAt = DateTimeOffset.UtcNow
        };

        await _blobStorage.UploadStateAsync(jobId, JsonSerializer.Serialize(response, JsonOptions), ct);

        _logger.LogInformation("Detect job {JobId} found {Count} PII instance(s).", jobId, entities.Count);
        return response;
    }

    public async Task<ApplyResponse> ApplyAsync(
        string jobId, IReadOnlyList<string> selectedEntityIds, CancellationToken ct = default)
    {
        var stateJson = await _blobStorage.DownloadStateAsync(jobId, ct)
            ?? throw new ArgumentException($"Unknown job '{jobId}'.");
        var state = JsonSerializer.Deserialize<DetectionResponse>(stateJson, JsonOptions)
            ?? throw new InvalidOperationException($"Corrupt job state for '{jobId}'.");

        var selectedSet = selectedEntityIds.ToHashSet(StringComparer.Ordinal);
        var selected = state.Entities.Where(e => selectedSet.Contains(e.Id)).ToList();

        var original = await _blobStorage.DownloadOriginalAsync(jobId, ct)
            ?? throw new ArgumentException($"Original document for job '{jobId}' not found.");

        var processor = ResolveProcessor(state.ContentType);

        _logger.LogInformation(
            "Apply job {JobId}: redacting {Selected} of {Total} instance(s).",
            jobId, selected.Count, state.Entities.Count);

        var redacted = await processor.RedactAsync(original.Content, selected, ct);

        var extension = Path.GetExtension(state.FileName);
        var redactedBlobName = $"redacted/{jobId}{extension}";
        var redactedUrl = await _blobStorage.UploadToRedactedAsync(
            redacted, redactedBlobName, state.ContentType, ct);

        return new ApplyResponse
        {
            JobId = jobId,
            RedactedUrl = redactedUrl,
            RedactedCount = selected.Count,
            FileName = state.FileName,
            ProcessedAt = DateTimeOffset.UtcNow
        };
    }

    public Task<BlobStream?> OpenOriginalAsync(string jobId, CancellationToken ct = default) =>
        _blobStorage.OpenOriginalDocumentAsync(jobId, ct);

    public Task<BlobStream?> OpenRedactedAsync(string jobId, CancellationToken ct = default) =>
        _blobStorage.OpenRedactedDocumentAsync(jobId, ct);

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<DetectedEntity> BuildEntities(
        IReadOnlyList<PiiEntity> raw, ExtractedDocument extracted)
    {
        var result = new List<DetectedEntity>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var e = raw[i];
            var boxes = MapBoxes(e.Offset, e.Length, extracted.Words);
            result.Add(new DetectedEntity
            {
                Id = $"e{i}",
                Text = e.Text,
                Category = e.Category,
                SubCategory = e.SubCategory,
                ConfidenceScore = e.ConfidenceScore,
                Offset = e.Offset,
                Length = e.Length,
                Boxes = boxes
            });
        }
        return result;
    }

    /// <summary>
    /// Maps an entity span to one bounding box per page by unioning the boxes of every word
    /// whose span overlaps it. Empty for TXT/DOCX (no word geometry).
    /// </summary>
    private static IReadOnlyList<DetectedBox> MapBoxes(int offset, int length, IReadOnlyList<LayoutWord> words)
    {
        if (words.Count == 0)
            return Array.Empty<DetectedBox>();

        var spanEnd = offset + length;
        var perPage = new Dictionary<int, (double MinX, double MinY, double MaxX, double MaxY)>();

        foreach (var word in words)
        {
            var wordEnd = word.Offset + word.Length;
            if (word.Offset >= spanEnd || offset >= wordEnd) continue; // no overlap

            var b = word.Box;
            var right = b.X + b.Width;
            var bottom = b.Y + b.Height;

            if (perPage.TryGetValue(b.Page, out var cur))
            {
                perPage[b.Page] = (
                    Math.Min(cur.MinX, b.X),
                    Math.Min(cur.MinY, b.Y),
                    Math.Max(cur.MaxX, right),
                    Math.Max(cur.MaxY, bottom));
            }
            else
            {
                perPage[b.Page] = (b.X, b.Y, right, bottom);
            }
        }

        return perPage
            .OrderBy(kv => kv.Key)
            .Select(kv => new DetectedBox
            {
                Page = kv.Key,
                X = kv.Value.MinX,
                Y = kv.Value.MinY,
                Width = kv.Value.MaxX - kv.Value.MinX,
                Height = kv.Value.MaxY - kv.Value.MinY
            })
            .ToList();
    }

    private IDocumentFormatProcessor ResolveProcessor(string contentType) =>
        _processors.FirstOrDefault(p => p.CanHandle(contentType))
        ?? throw new ArgumentException(
            $"Unsupported file type '{contentType}'. Supported: PDF, DOCX, and TXT.");

    private static void ValidateFile(IFormFile file)
    {
        if (file is null || file.Length == 0)
            throw new ArgumentException("No file provided or file is empty.");

        if (file.Length > MaxFileSizeBytes)
            throw new ArgumentException("File exceeds the maximum allowed size of 50 MB.");

        if (!SupportedContentTypes.Contains(file.ContentType))
            throw new ArgumentException(
                $"Unsupported file type '{file.ContentType}'. Supported: PDF, DOCX, and TXT.");
    }
}
