using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

public interface IDocumentRedactionOrchestrator
{
    Task<RedactionResponse> ProcessAsync(IFormFile file, CancellationToken ct = default);
}
