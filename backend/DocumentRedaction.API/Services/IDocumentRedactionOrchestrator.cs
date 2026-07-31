using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

public interface IDocumentRedactionOrchestrator
{
    /// <summary>
    /// DETECT phase: stores the upload, extracts its text, detects every PII instance, and
    /// returns them for review. Nothing is redacted yet.
    /// </summary>
    Task<DetectionResponse> DetectAsync(IFormFile file, CancellationToken ct = default);

    /// <summary>
    /// APPLY phase: redacts only the selected instances, preserving the original format,
    /// and stores the result.
    /// </summary>
    Task<ApplyResponse> ApplyAsync(string jobId, IReadOnlyList<string> selectedEntityIds, CancellationToken ct = default);

    /// <summary>Opens the original uploaded document for a job, or null if not found.</summary>
    Task<BlobStream?> OpenOriginalAsync(string jobId, CancellationToken ct = default);

    /// <summary>Opens the redacted document for a job, or null if not found.</summary>
    Task<BlobStream?> OpenRedactedAsync(string jobId, CancellationToken ct = default);
}
