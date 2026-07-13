using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

/// <summary>
/// A raw PII entity as returned by the Azure Language Text PII endpoint, with offsets
/// expressed in UTF-16 code units so they index a .NET <see cref="string"/> directly.
/// </summary>
public record PiiEntity(
    string Text,
    string Category,
    string? SubCategory,
    double ConfidenceScore,
    int Offset,
    int Length);

/// <summary>
/// A single word located on a page, produced by Azure Document Intelligence for PDF
/// documents. <see cref="Offset"/>/<see cref="Length"/> index the extracted text so an
/// entity span can be mapped back to its on-page box(es).
/// </summary>
public record LayoutWord(int Offset, int Length, DetectedBox Box);

/// <summary>
/// The text extracted from a document, plus optional page geometry and per-word boxes
/// (populated only for PDFs). The offsets in <see cref="Words"/> and the entity offsets
/// returned by the Text PII endpoint both index <see cref="Text"/>.
/// </summary>
public record ExtractedDocument(
    string Text,
    IReadOnlyList<PageInfo> Pages,
    IReadOnlyList<LayoutWord> Words);

/// <summary>A contiguous character span to redact, in <see cref="ExtractedDocument.Text"/> coordinates.</summary>
public record RedactionSpan(int Offset, int Length);

/// <summary>
/// Handles text extraction and format-preserving redaction for one document format.
/// Detection is uniform (Text PII over the extracted text); only extraction and the
/// final burn differ per format.
/// </summary>
public interface IDocumentFormatProcessor
{
    /// <summary>True if this processor handles the given MIME type.</summary>
    bool CanHandle(string contentType);

    /// <summary>Extracts text (and, for PDF, page geometry + word boxes) from the document.</summary>
    Task<ExtractedDocument> ExtractAsync(BinaryData content, CancellationToken ct = default);

    /// <summary>
    /// Produces a redacted copy of <paramref name="original"/> in the same format, masking
    /// only the given entities. TXT/DOCX use each entity's character offset/length; PDF uses
    /// each entity's page boxes — so no re-extraction (or second Document Intelligence call)
    /// is needed at apply time.
    /// </summary>
    Task<BinaryData> RedactAsync(
        BinaryData original,
        IReadOnlyList<Models.DetectedEntity> selectedEntities,
        CancellationToken ct = default);
}
