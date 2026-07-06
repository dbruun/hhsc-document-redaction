namespace DocumentRedaction.API.Services;

public interface IDocumentExtractionService
{
    /// <summary>
    /// Submits a document to Azure AI Document Intelligence and returns
    /// the full extracted plain text.
    /// </summary>
    Task<string> ExtractTextAsync(Stream documentStream, string contentType, CancellationToken ct = default);
}
