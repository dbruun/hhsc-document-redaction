using DocumentRedaction.API.Models;

namespace DocumentRedaction.API.Services;

public interface IPiiRedactionService
{
    /// <summary>
    /// Detects PII entities in <paramref name="text"/>, replaces each with its
    /// category label, and returns both the redacted text and the entity list.
    /// </summary>
    Task<(string RedactedText, IReadOnlyList<RedactedEntity> Entities)> RedactAsync(
        string text,
        CancellationToken ct = default);
}
