using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Submits a native document to the Azure AI Language document PII redaction API
/// and returns the redacted text content together with per-entity metadata.
/// </summary>
public interface IDocumentPiiRedactionService
{
    /// <summary>
    /// Asynchronously redacts PII in the document stored at
    /// <paramref name="sourceBlobName"/> and returns the result.
    /// </summary>
    /// <param name="sourceBlobName">
    /// The blob name (within the managed container) of the original document.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<DocumentPiiResult> RedactDocumentAsync(
        string sourceBlobName,
        CancellationToken ct = default);
}

/// <summary>
/// Output produced by a single document PII redaction job.
/// </summary>
public record DocumentPiiResult
{
    /// <summary>
    /// The extracted text content of the document with all detected PII
    /// replaced by the same number of asterisk characters (<c>*</c>).
    /// </summary>
    public required string RedactedText { get; init; }

    /// <summary>Per-entity metadata returned by the service.</summary>
    public required IReadOnlyList<RedactedEntity> Entities { get; init; }
}
