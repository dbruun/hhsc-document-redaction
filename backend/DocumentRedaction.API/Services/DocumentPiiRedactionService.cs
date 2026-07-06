using Azure.Core;
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

    /// Entra ID scope for the Azure AI Language (Cognitive Services) data plane.
    private static readonly string[] CognitiveServicesScopes = { "https://cognitiveservices.azure.com/.default" };

    /// SAS validity window – the Language native-document endpoint requires the SAS
    /// tokens to be valid for at least 24 hours.
    private static readonly TimeSpan SasValidity = TimeSpan.FromHours(25);

    private readonly IBlobStorageService _blobStorage;
    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly string _languageEndpoint;
    private readonly ILogger<DocumentPiiRedactionService> _logger;

    public DocumentPiiRedactionService(
        IBlobStorageService blobStorage,
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IConfiguration config,
        ILogger<DocumentPiiRedactionService> logger)
    {
        _blobStorage = blobStorage;
        _credential = credential;
        _logger = logger;

        _languageEndpoint = (config["Azure:Language:Endpoint"]
            ?? throw new InvalidOperationException("Azure:Language:Endpoint is required."))
            .TrimEnd('/');

        _http = httpClientFactory.CreateClient(nameof(DocumentPiiRedactionService));
    }

    /// <summary>
    /// Acquires an Entra ID bearer token for the Azure AI Language data plane.
    /// The Language resource has local (key) auth disabled, so this is the only
    /// supported authentication method.
    /// </summary>
    private async Task<string> GetBearerTokenAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(CognitiveServicesScopes), ct);
        return token.Token;
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
        var sourceSasUrl = await _blobStorage.GenerateBlobSasUrlAsync(
            sourceBlobName,
            BlobSasPermissions.Read | BlobSasPermissions.List,
            SasValidity,
            ct);

        var targetSasUrl = await _blobStorage.GenerateContainerSasUrlAsync(
            BlobContainerSasPermissions.Write | BlobContainerSasPermissions.Create | BlobContainerSasPermissions.List,
            SasValidity,
            ct);

        // Submit the async job
        var operationLocation = await SubmitJobAsync(sourceSasUrl, targetSasUrl, ct);
        _logger.LogInformation("Document PII job submitted; polling {OperationLocation}", operationLocation);

        // Poll until the job finishes
        var jobResult = await PollJobAsync(operationLocation, ct);

        // The Language service writes the redacted document plus a .result.json
        // (entity metadata) to the target container.
        var (resultJsonUrl, redactedDocumentUrl) = ExtractTargets(jobResult);
        _logger.LogInformation("Downloading result JSON from {Url}", resultJsonUrl);

        var resultJson = await _blobStorage.DownloadTextBlobAsync(resultJsonUrl, ct);
        return ParseResultJson(resultJson, redactedDocumentUrl);
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetBearerTokenAsync(ct));
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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetBearerTokenAsync(ct));

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
    /// Locates the two blobs the Language service wrote for the document: the
    /// <c>.result.json</c> (entity metadata) and the redacted document itself.
    /// </summary>
    private static (string ResultJsonUrl, string RedactedDocumentUrl) ExtractTargets(JsonElement jobRoot)
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

                string? resultJsonUrl = null;
                string? redactedDocumentUrl = null;

                foreach (var target in targets.EnumerateArray())
                {
                    if (!target.TryGetProperty("location", out var loc)) continue;
                    var location = loc.GetString() ?? string.Empty;

                    if (location.EndsWith("result.json", StringComparison.OrdinalIgnoreCase))
                        resultJsonUrl = location;
                    else
                        redactedDocumentUrl = location;
                }

                if (resultJsonUrl is not null)
                    return (resultJsonUrl, redactedDocumentUrl ?? resultJsonUrl);
            }
        }

        throw new InvalidOperationException(
            "Could not locate a 'result.json' target in the Language service job result.");
    }

    /// <summary>
    /// Parses the <c>.result.json</c> blob that the Language service wrote to the
    /// target container and extracts the detected PII entities. The native-document
    /// endpoint returns the entity list (original <c>text</c>, entity <c>type</c> and
    /// character <c>mask</c>) but not the full document text — the redacted document
    /// itself is written to a separate target blob.
    /// </summary>
    private static DocumentPiiResult ParseResultJson(string resultJson, string redactedDocumentUrl)
    {
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        // The result.json root is the document object (has an "entities" array directly).
        // Older/nested layouts wrap it under documents[] / results.documents[].
        var docEl = root;
        if (!root.TryGetProperty("entities", out _))
        {
            if (root.TryGetProperty("documents", out var docsFlat) && docsFlat.GetArrayLength() > 0)
                docEl = docsFlat[0];
            else if (root.TryGetProperty("results", out var resultsEl)
                && resultsEl.TryGetProperty("documents", out var docsNested)
                && docsNested.GetArrayLength() > 0)
                docEl = docsNested[0];
        }

        var entities = new List<RedactedEntity>();
        if (docEl.TryGetProperty("entities", out var entitiesEl))
        {
            foreach (var entity in entitiesEl.EnumerateArray())
            {
                entities.Add(new RedactedEntity
                {
                    Text = GetString(entity, "text"),
                    // The native-document schema uses "type"; older schemas use "category".
                    Category = entity.TryGetProperty("type", out var ty)
                        ? ty.GetString() ?? string.Empty
                        : GetString(entity, "category"),
                    SubCategory = entity.TryGetProperty("subCategory", out var sc) ? sc.GetString() : null,
                    ConfidenceScore = entity.TryGetProperty("confidenceScore", out var cs) ? cs.GetDouble() : 0,
                    Offset = entity.TryGetProperty("offset", out var o) ? o.GetInt32() : 0,
                    Length = entity.TryGetProperty("length", out var l) ? l.GetInt32() : 0
                });
            }
        }

        // The native-document endpoint does not return the full document text in the
        // result.json, so build an aligned, line-per-entity view of the detected PII
        // and its character mask for the side-by-side comparison in the UI.
        var extracted = new StringBuilder();
        var redacted = new StringBuilder();
        foreach (var entity in entities)
        {
            var maskLength = entity.Length > 0 ? entity.Length : entity.Text.Length;
            var mask = new string('*', maskLength);
            extracted.Append('[').Append(entity.Category).Append("] ").AppendLine(entity.Text);
            redacted.Append('[').Append(entity.Category).Append("] ").AppendLine(mask);
        }

        return new DocumentPiiResult
        {
            ExtractedText = extracted.ToString().TrimEnd(),
            RedactedText = redacted.ToString().TrimEnd(),
            RedactedDocumentUrl = redactedDocumentUrl,
            Entities = entities.AsReadOnly()
        };
    }

    private static string GetString(JsonElement el, string propertyName) =>
        el.TryGetProperty(propertyName, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;
}
