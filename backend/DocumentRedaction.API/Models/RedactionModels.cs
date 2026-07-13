namespace DocumentRedaction.API.Models;

/// <summary>
/// A normalized bounding box (0..1 relative to page width/height) for a piece of
/// detected PII. Only populated for PDF documents, where Azure Document Intelligence
/// provides per-word coordinates.
/// </summary>
public record DetectedBox
{
    /// <summary>1-based page number the box appears on.</summary>
    public required int Page { get; init; }

    /// <summary>Left edge, 0..1 of page width.</summary>
    public required double X { get; init; }

    /// <summary>Top edge, 0..1 of page height.</summary>
    public required double Y { get; init; }

    /// <summary>Width, 0..1 of page width.</summary>
    public required double Width { get; init; }

    /// <summary>Height, 0..1 of page height.</summary>
    public required double Height { get; init; }
}

/// <summary>Physical size of a rendered page (PDF only), in Document Intelligence units (inches).</summary>
public record PageInfo
{
    public required int Page { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}

/// <summary>
/// A single detected PII instance. Each instance is independently selectable so the
/// reviewer can redact, for example, a nurse and a doctor while keeping the patient —
/// even though all three share the <c>Person</c> category.
/// </summary>
public record DetectedEntity
{
    /// <summary>Stable identifier for this instance within the job (e.g. "e0", "e1").</summary>
    public required string Id { get; init; }

    public required string Text { get; init; }
    public required string Category { get; init; }
    public required string? SubCategory { get; init; }
    public required double ConfidenceScore { get; init; }

    /// <summary>Character offset into <see cref="DetectionResponse.ExtractedText"/>.</summary>
    public required int Offset { get; init; }

    /// <summary>Character length of the span.</summary>
    public required int Length { get; init; }

    /// <summary>Page boxes for this instance (PDF only; empty for TXT/DOCX).</summary>
    public required IReadOnlyList<DetectedBox> Boxes { get; init; }
}

/// <summary>
/// Result of the DETECT phase: the extracted text plus every detected PII instance,
/// returned to the client so the reviewer can preview and select what to redact.
/// </summary>
public record DetectionResponse
{
    public required string JobId { get; init; }
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string ContentType { get; init; }

    /// <summary>The full text extracted from the document, used for the highlight preview.</summary>
    public required string ExtractedText { get; init; }

    public required int PageCount { get; init; }
    public required IReadOnlyList<PageInfo> Pages { get; init; }
    public required IReadOnlyList<DetectedEntity> Entities { get; init; }
    public required DateTimeOffset ProcessedAt { get; init; }
}

/// <summary>Body of the APPLY request: the instance ids the reviewer chose to redact.</summary>
public record ApplyRequest
{
    public required IReadOnlyList<string> SelectedEntityIds { get; init; }
}

/// <summary>Result of the APPLY phase.</summary>
public record ApplyResponse
{
    public required string JobId { get; init; }
    public required string RedactedUrl { get; init; }
    public required int RedactedCount { get; init; }
    public required string FileName { get; init; }
    public required DateTimeOffset ProcessedAt { get; init; }
}

public record ErrorResponse
{
    public required string Error { get; init; }
    public string? Details { get; init; }
}
