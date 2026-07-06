using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using DocumentRedaction.API.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Submits a document to the Azure AI Language
/// <c>/language/analyze-documents/jobs</c> endpoint for native PII detection
/// and redaction, then polls until the job is complete and returns the result.
///
/// The Language service:
/// 1. Reads the source document (via SAS URL) from Azure Blob Storage.
/// 2. Extracts the text content.
/// 3. Detects and redacts PII entities (character-mask policy by default).
/// 4. Writes the redacted binary document and a <c>.result.json</c> file to
///    the target container.
///
/// This service downloads the <c>.result.json</c> to surface the redacted text
/// and entity metadata to the rest of the application.
/// </summary>
public sealed class DocumentPiiRedactionService : IDocumentPiiRedactionService
{
    private const string ApiVersion = "2024-11-15-preview";

    /// SAS validity window – long enough for large documents to process.
    private static readonly TimeSpan SasValidity = TimeSpan.FromHours(1);

    private readonly IBlobStorageService _blobStorage;
    private readonly HttpClient _http;
    private readonly string _languageEndpoint;
    private readonly string _languageKey;
    private readonly ILogger<DocumentPiiRedactionService> _logger;

    public DocumentPiiRedactionService(
        IBlobStorageService blobStorage,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<DocumentPiiRedactionService> logger)
    {
        _blobStorage = blobStorage;
        _logger = logger;

        _languageEndpoint = (config["Azure:Language:Endpoint"]
            ?? throw new InvalidOperationException("Azure:Language:Endpoint is required."))
            .TrimEnd('/');

        _languageKey = config["Azure:Language:Key"]
            ?? throw new InvalidOperationException("Azure:Language:Key is required.");

        _http = httpClientFactory.CreateClient(nameof(DocumentPiiRedactionService));
    }

    /// <inheritdoc/>
    public async Task<DocumentPiiResult> RedactDocumentAsync(
        string sourceBlobName,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting document PII redaction for blob '{BlobName}'", sourceBlobName);

        // Generate SAS URLs:
        //  • source blob  – read access so the Language service can read the file
        //  • target container – write + create + list so the service can write results
        var sourceSasUrl = _blobStorage.GenerateBlobSasUrl(
            sourceBlobName,
            BlobSasPermissions.Read | BlobSasPermissions.List,
            SasValidity);

        var targetSasUrl = _blobStorage.GenerateContainerSasUrl(
            BlobContainerSasPermissions.Write | BlobContainerSasPermissions.Create | BlobContainerSasPermissions.List,
            SasValidity);

        // Submit the async job
        var operationLocation = await SubmitJobAsync(sourceSasUrl, targetSasUrl, ct);
        _logger.LogInformation("Document PII job submitted; polling {OperationLocation}", operationLocation);

        // Poll until the job finishes
        var jobResult = await PollJobAsync(operationLocation, ct);

        // The Language service writes a .result.json with entity data
        var resultJsonUrl = ExtractResultJsonUrl(jobResult);
        _logger.LogInformation("Downloading result JSON from {Url}", resultJsonUrl);

        var resultJson = await _blobStorage.DownloadTextBlobAsync(resultJsonUrl, ct);
        return ParseResultJson(resultJson);
    }

    // ─── Private helpers ────────────────────────────────────────────────────────

    private async Task<string> SubmitJobAsync(
        string sourceSasUrl,
        string targetSasUrl,
        CancellationToken ct)
    {
        var payload = new
        {
            displayName = "DocumentPiiRedaction",
            analysisInput = new
            {
                documents = new[]
                {
                    new
                    {
                        id = "1",
                        language = "en-US",
                        source = new { location = sourceSasUrl },
                        target = new { location = targetSasUrl }
                    }
                }
            },
            tasks = new[]
            {
                new
                {
                    kind = "PiiEntityRecognition",
                    taskName = "Redact",
                    parameters = new
                    {
                        redactionPolicy = new { policyKind = "characterMask" }
                    }
                }
            }
        };

        var url = $"{_languageEndpoint}/language/analyze-documents/jobs?api-version={ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Ocp-Apim-Subscription-Key", _languageKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Language service returned {(int)response.StatusCode} when submitting job: {errorBody}");
        }

        if (!response.Headers.TryGetValues("operation-location", out var locations))
            throw new InvalidOperationException(
                "Language service did not return an 'operation-location' header.");

        return locations.First();
    }

    private async Task<JsonElement> PollJobAsync(string operationLocation, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        const int maxAttempts = 90; // up to ~3 minutes with back-off

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await Task.Delay(delay, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            request.Headers.Add("Ocp-Apim-Subscription-Key", _languageKey);

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Language service returned {(int)response.StatusCode} while polling job: {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.GetProperty("status").GetString() ?? "unknown";
            _logger.LogDebug("Document PII job status: {Status}", status);

            switch (status)
            {
                case "succeeded":
                    return root;

                case "failed":
                case "cancelled":
                    var errMsg = root.TryGetProperty("errors", out var errs)
                        ? errs.GetRawText()
                        : "(no error details)";
                    throw new InvalidOperationException(
                        $"Document PII job ended with status '{status}': {errMsg}");
            }

            // Progressive back-off: 2 s → 4 s → 8 s (cap)
            if (delay < TimeSpan.FromSeconds(8))
                delay = delay * 2;
        }

        throw new TimeoutException(
            "Document PII job did not complete within the allotted time.");
    }

    /// <summary>
    /// Finds the URL of the <c>.result.json</c> blob within the job result payload.
    /// </summary>
    private static string ExtractResultJsonUrl(JsonElement jobRoot)
    {
        var items = jobRoot
            .GetProperty("tasks")
            .GetProperty("items")
            .EnumerateArray();

        foreach (var item in items)
        {
            if (!item.TryGetProperty("results", out var results)) continue;
            if (!results.TryGetProperty("documents", out var documents)) continue;

            foreach (var doc in documents.EnumerateArray())
            {
                if (!doc.TryGetProperty("targets", out var targets)) continue;

                foreach (var target in targets.EnumerateArray())
                {
                    if (!target.TryGetProperty("location", out var loc)) continue;
                    var location = loc.GetString() ?? string.Empty;

                    // The .result.json file ends with "result.json"
                    if (location.Contains("result.json", StringComparison.OrdinalIgnoreCase))
                        return location;
                }
            }
        }

        throw new InvalidOperationException(
            "Could not locate a 'result.json' target in the Language service job result.");
    }

    /// <summary>
    /// Parses the <c>.result.json</c> blob that the Language service wrote to
    /// the target container and extracts the redacted text and entity list.
    /// </summary>
    private static DocumentPiiResult ParseResultJson(string resultJson)
    {
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        // Navigate to the first document result.
        // Possible layouts (the schema has evolved across API versions):
        //   { documents: [{ redactedText, entities }] }
        //   { results: { documents: [{ redactedContent: { content }, entities }] } }
        JsonElement? firstDoc = null;

        if (root.TryGetProperty("documents", out var docsFlat) && docsFlat.GetArrayLength() > 0)
        {
            firstDoc = docsFlat[0];
        }
        else if (root.TryGetProperty("results", out var resultsEl)
            && resultsEl.TryGetProperty("documents", out var docsNested)
            && docsNested.GetArrayLength() > 0)
        {
            firstDoc = docsNested[0];
        }

        if (firstDoc is null)
        {
            return new DocumentPiiResult
            {
                RedactedText = string.Empty,
                Entities = Array.Empty<RedactedEntity>()
            };
        }

        var docEl = firstDoc.Value;

        // Extract redacted text (field name varies by API version)
        var redactedText = string.Empty;
        if (docEl.TryGetProperty("redactedText", out var rtEl))
            redactedText = rtEl.GetString() ?? string.Empty;
        else if (docEl.TryGetProperty("redactedContent", out var rcEl)
            && rcEl.TryGetProperty("content", out var rcContent))
            redactedText = rcContent.GetString() ?? string.Empty;

        // Extract entity list
        var entities = new List<RedactedEntity>();
        if (docEl.TryGetProperty("entities", out var entitiesEl))
        {
            foreach (var entity in entitiesEl.EnumerateArray())
            {
                entities.Add(new RedactedEntity
                {
                    Text = GetString(entity, "text"),
                    Category = GetString(entity, "category"),
                    SubCategory = entity.TryGetProperty("subCategory", out var sc) ? sc.GetString() : null,
                    ConfidenceScore = entity.TryGetProperty("confidenceScore", out var cs) ? cs.GetDouble() : 0,
                    Offset = entity.TryGetProperty("offset", out var o) ? o.GetInt32() : 0,
                    Length = entity.TryGetProperty("length", out var l) ? l.GetInt32() : 0
                });
            }
        }

        return new DocumentPiiResult
        {
            RedactedText = redactedText,
            Entities = entities.AsReadOnly()
        };
    }

    private static string GetString(JsonElement el, string propertyName) =>
        el.TryGetProperty(propertyName, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;
}
