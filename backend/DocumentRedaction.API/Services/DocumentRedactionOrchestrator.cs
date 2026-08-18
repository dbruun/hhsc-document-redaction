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

        // Per-word boxes (PDF only) so the reviewer can click a word the models missed and add
        // it as a manual redaction. Text is sliced from the extracted content for the tooltip.
        var words = new List<LayoutWordInfo>(extracted.Words.Count);
        foreach (var w in extracted.Words)
        {
            words.Add(new LayoutWordInfo
            {
                Page = w.Box.Page,
                X = w.Box.X,
                Y = w.Box.Y,
                Width = w.Box.Width,
                Height = w.Box.Height,
                Text = SafeSlice(extracted.Text, w.Offset, w.Length)
            });
        }

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
            Words = words,
            ProcessedAt = DateTimeOffset.UtcNow
        };

        await _blobStorage.UploadStateAsync(jobId, JsonSerializer.Serialize(response, JsonOptions), ct);

        _logger.LogInformation("Detect job {JobId} found {Count} PII instance(s).", jobId, entities.Count);
        return response;
    }

    public async Task<ApplyResponse> ApplyAsync(
        string jobId, IReadOnlyList<string> selectedEntityIds, IReadOnlyList<DetectedBox> manualBoxes, CancellationToken ct = default)
    {
        var stateJson = await _blobStorage.DownloadStateAsync(jobId, ct)
            ?? throw new ArgumentException($"Unknown job '{jobId}'.");
        var state = JsonSerializer.Deserialize<DetectionResponse>(stateJson, JsonOptions)
            ?? throw new InvalidOperationException($"Corrupt job state for '{jobId}'.");

        var selectedSet = selectedEntityIds.ToHashSet(StringComparer.Ordinal);
        var selected = state.Entities.Where(e => selectedSet.Contains(e.Id)).ToList();

        // Manual boxes the reviewer added by clicking missed words become synthetic single-box
        // entities so the per-format redactor treats them identically to detected instances.
        var manual = (manualBoxes ?? Array.Empty<DetectedBox>())
            .Select((box, i) => new DetectedEntity
            {
                Id = $"m{i}",
                Text = string.Empty,
                Category = "Manual",
                SubCategory = null,
                ConfidenceScore = 1.0,
                Offset = 0,
                Length = 0,
                Boxes = new[] { box }
            })
            .ToList();

        var toRedact = selected.Concat(manual).ToList();

        var original = await _blobStorage.DownloadOriginalAsync(jobId, ct)
            ?? throw new ArgumentException($"Original document for job '{jobId}' not found.");

        var processor = ResolveProcessor(state.ContentType);

        _logger.LogInformation(
            "Apply job {JobId}: redacting {Selected} detected + {Manual} manual instance(s).",
            jobId.Replace('\r', '_').Replace('\n', '_'), selected.Count, manual.Count);

        var redacted = await processor.RedactAsync(original.Content, toRedact, ct);

        var extension = Path.GetExtension(state.FileName);
        var redactedBlobName = $"redacted/{jobId}{extension}";
        var redactedUrl = await _blobStorage.UploadToRedactedAsync(
            redacted, redactedBlobName, state.ContentType, ct);

        return new ApplyResponse
        {
            JobId = jobId,
            RedactedUrl = redactedUrl,
            RedactedCount = toRedact.Count,
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
    /// Maps an entity span to one bounding box per text line per page by unioning the boxes of
    /// every matched word within the same line. Two words are considered on the same line when
    /// the center-Y of one falls within the vertical extent of the other (with a tolerance of
    /// half the minimum word height). This prevents a multi-line or multi-column entity from
    /// producing a single giant box that blacks out unrelated content between the words.
    /// Empty for TXT/DOCX (no word geometry).
    /// </summary>
    private static IReadOnlyList<DetectedBox> MapBoxes(int offset, int length, IReadOnlyList<LayoutWord> words)
    {
        if (words.Count == 0)
            return Array.Empty<DetectedBox>();

        var spanEnd = offset + length;

        // Collect words whose character span overlaps the entity span.
        var matched = new List<LayoutWord>();
        foreach (var word in words)
        {
            var wordEnd = word.Offset + word.Length;
            if (word.Offset >= spanEnd || offset >= wordEnd) continue; // no overlap
            matched.Add(word);
        }

        if (matched.Count == 0)
            return Array.Empty<DetectedBox>();

        // Group matched words into per-line buckets. Two words belong to the same line when
        // they share the same page and the center-Y of one falls within the [Y, Y+Height]
        // band of the other (using a tolerance of 0.5 × min word height).
        var lineBuckets = new List<(int Page, double MinX, double MinY, double MaxX, double MaxY)>();

        foreach (var word in matched)
        {
            var b = word.Box;
            var centerY = b.Y + b.Height / 2.0;
            var right = b.X + b.Width;
            var bottom = b.Y + b.Height;

            var merged = false;
            for (var i = 0; i < lineBuckets.Count; i++)
            {
                var line = lineBuckets[i];
                if (line.Page != b.Page) continue;

                // Tolerance: half the minimum height of the two words.
                var lineHeight = line.MaxY - line.MinY;
                var tolerance = Math.Min(b.Height, lineHeight) * 0.5;
                var lineCenterY = (line.MinY + line.MaxY) / 2.0;

                // Merge if this word's center-Y is within the bucket's vertical band (±tolerance).
                if (centerY >= line.MinY - tolerance && centerY <= line.MaxY + tolerance)
                {
                    lineBuckets[i] = (
                        line.Page,
                        Math.Min(line.MinX, b.X),
                        Math.Min(line.MinY, b.Y),
                        Math.Max(line.MaxX, right),
                        Math.Max(line.MaxY, bottom));
                    merged = true;
                    break;
                }
                // Also merge if the bucket's center-Y is within this word's band (±tolerance).
                if (lineCenterY >= b.Y - tolerance && lineCenterY <= bottom + tolerance)
                {
                    lineBuckets[i] = (
                        line.Page,
                        Math.Min(line.MinX, b.X),
                        Math.Min(line.MinY, b.Y),
                        Math.Max(line.MaxX, right),
                        Math.Max(line.MaxY, bottom));
                    merged = true;
                    break;
                }
            }

            if (!merged)
                lineBuckets.Add((b.Page, b.X, b.Y, right, bottom));
        }

        return lineBuckets
            .OrderBy(l => l.Page)
            .ThenBy(l => l.MinY)
            .ThenBy(l => l.MinX)
            .Select(l => new DetectedBox
            {
                Page = l.Page,
                X = l.MinX,
                Y = l.MinY,
                Width = l.MaxX - l.MinX,
                Height = l.MaxY - l.MinY
            })
            .ToList();
    }

    private IDocumentFormatProcessor ResolveProcessor(string contentType) =>
        _processors.FirstOrDefault(p => p.CanHandle(contentType))
        ?? throw new ArgumentException(
            $"Unsupported file type '{contentType}'. Supported: PDF, DOCX, and TXT.");

    private static string SafeSlice(string text, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset >= text.Length)
            return string.Empty;
        var end = Math.Min(offset + length, text.Length);
        return text[offset..end];
    }

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
