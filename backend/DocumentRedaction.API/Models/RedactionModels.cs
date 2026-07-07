namespace DocumentRedaction.API.Models;

public record RedactionRequest
{
    public required IFormFile File { get; init; }
}

public record RedactionResponse
{
    public required string JobId { get; init; }
    public required string OriginalBlobUrl { get; init; }
    public required string RedactedBlobUrl { get; init; }
    public required string ContentType { get; init; }
    public required string ExtractedText { get; init; }
    public required string RedactedText { get; init; }
    public required IReadOnlyList<RedactedEntity> RedactedEntities { get; init; }
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }
    public required DateTimeOffset ProcessedAt { get; init; }
}

public record RedactedEntity
{
    public required string Text { get; init; }
    public required string Category { get; init; }
    public required string? SubCategory { get; init; }
    public required double ConfidenceScore { get; init; }
    public required int Offset { get; init; }
    public required int Length { get; init; }
}

public record ErrorResponse
{
    public required string Error { get; init; }
    public string? Details { get; init; }
}
