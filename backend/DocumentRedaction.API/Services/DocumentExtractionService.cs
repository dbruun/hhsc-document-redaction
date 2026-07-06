using Azure;
using Azure.AI.DocumentIntelligence;
using System.Text;

namespace DocumentRedaction.API.Services;

public sealed class DocumentExtractionService : IDocumentExtractionService
{
    private readonly DocumentIntelligenceClient _client;
    private readonly ILogger<DocumentExtractionService> _logger;

    public DocumentExtractionService(IConfiguration config, ILogger<DocumentExtractionService> logger)
    {
        _logger = logger;

        var endpoint = config["Azure:DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("Azure:DocumentIntelligence:Endpoint is required.");
        var key = config["Azure:DocumentIntelligence:Key"]
            ?? throw new InvalidOperationException("Azure:DocumentIntelligence:Key is required.");

        _client = new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(key));
    }

    public async Task<string> ExtractTextAsync(
        Stream documentStream,
        string contentType,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Submitting document to Azure AI Document Intelligence for text extraction");

        var bytes = await ReadAllBytesAsync(documentStream, ct);
        var options = new AnalyzeDocumentOptions("prebuilt-read", BinaryData.FromBytes(bytes));

        var operation = await _client.AnalyzeDocumentAsync(WaitUntil.Completed, options, ct);
        var result = operation.Value;

        _logger.LogInformation(
            "Extraction complete: {PageCount} page(s), {ParagraphCount} paragraph(s)",
            result.Pages.Count,
            result.Paragraphs?.Count ?? 0);

        // AnalyzeResult.Content contains the full extracted text in reading order.
        // If paragraphs are available, build a structured version with proper line breaks.
        if (result.Paragraphs is { Count: > 0 })
        {
            var sb = new StringBuilder();
            foreach (var paragraph in result.Paragraphs)
            {
                sb.AppendLine(paragraph.Content);
                sb.AppendLine();
            }
            return sb.ToString().Trim();
        }

        // Fallback: use the raw Content string from the result
        return result.Content ?? string.Empty;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
