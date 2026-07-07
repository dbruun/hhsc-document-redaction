using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

public interface IDocumentRedactionOrchestrator
{
    Task<RedactionResponse> ProcessAsync(IFormFile file, CancellationToken ct = default);

    /// <summary>Opens the original uploaded document for a job, or null if not found.</summary>
    Task<BlobStream?> OpenOriginalAsync(string jobId, CancellationToken ct = default);

    /// <summary>Opens the redacted document for a job, or null if not found.</summary>
    Task<BlobStream?> OpenRedactedAsync(string jobId, CancellationToken ct = default);
}
