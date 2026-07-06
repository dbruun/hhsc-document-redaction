using Azure;
using Azure.AI.TextAnalytics;
using DocumentRedaction.API.Models;
using System.Text;

namespace DocumentRedaction.API.Services;

public sealed class PiiRedactionService : IPiiRedactionService
{
    // Azure AI Language supports a maximum of 5,120 characters per document in a single call.
    private const int MaxCharsPerDocument = 5120;

    private readonly TextAnalyticsClient _client;
    private readonly ILogger<PiiRedactionService> _logger;

    public PiiRedactionService(IConfiguration config, ILogger<PiiRedactionService> logger)
    {
        _logger = logger;

        var endpoint = config["Azure:Language:Endpoint"]
            ?? throw new InvalidOperationException("Azure:Language:Endpoint is required.");
        var key = config["Azure:Language:Key"]
            ?? throw new InvalidOperationException("Azure:Language:Key is required.");

        _client = new TextAnalyticsClient(new Uri(endpoint), new AzureKeyCredential(key));
    }

    public async Task<(string RedactedText, IReadOnlyList<RedactedEntity> Entities)> RedactAsync(
        string text,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting PII detection on {CharCount} character(s)", text.Length);

        // Split into chunks that respect the service character limit
        var chunks = SplitIntoChunks(text, MaxCharsPerDocument);
        var allEntities = new List<RedactedEntity>();
        var redactedBuilder = new StringBuilder();
        var charOffset = 0;

        foreach (var chunk in chunks)
        {
            var response = await _client.RecognizePiiEntitiesAsync(chunk, "en", cancellationToken: ct);
            var piiResult = response.Value;

            // Collect entities, adjusting their offsets to reflect position in the full document
            foreach (var entity in piiResult)
            {
                allEntities.Add(new RedactedEntity
                {
                    Text = entity.Text,
                    Category = entity.Category.ToString(),
                    SubCategory = entity.SubCategory,
                    ConfidenceScore = entity.ConfidenceScore,
                    Offset = entity.Offset + charOffset,
                    Length = entity.Length
                });
            }

            // The SDK returns the redacted text directly; append it to the full result
            redactedBuilder.Append(piiResult.RedactedText);
            charOffset += chunk.Length;
        }

        var redactedText = redactedBuilder.ToString();

        _logger.LogInformation(
            "PII redaction complete: {EntityCount} entity/entities redacted",
            allEntities.Count);

        return (redactedText, allEntities.AsReadOnly());
    }

    private static List<string> SplitIntoChunks(string text, int maxChars)
    {
        var chunks = new List<string>();

        for (var i = 0; i < text.Length; i += maxChars)
        {
            var length = Math.Min(maxChars, text.Length - i);

            // Try not to split in the middle of a word; walk back to the nearest whitespace
            if (i + length < text.Length)
            {
                var breakPoint = text.LastIndexOf(' ', i + length - 1, Math.Min(length, 200));
                if (breakPoint > i)
                    length = breakPoint - i + 1;
            }

            chunks.Add(text.Substring(i, length));
        }

        return chunks;
    }
}
