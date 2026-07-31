using Azure.Core;
using DocumentRedaction.API.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Extracts text and per-word page coordinates from a PDF using the Azure AI Document
/// Intelligence <c>prebuilt-read</c> model. The word offsets index the returned content
/// in UTF-16 code units, so PII entity spans (also detected over the same content) map
/// directly back to on-page boxes.
/// </summary>
public interface IDocumentLayoutService
{
    Task<ExtractedDocument> AnalyzeAsync(BinaryData pdf, CancellationToken ct = default);
}

public sealed class DocumentLayoutService : IDocumentLayoutService
{
    private const string ApiVersion = "2024-11-30";
    private static readonly string[] CognitiveServicesScopes = { "https://cognitiveservices.azure.com/.default" };

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly string _endpoint;
    private readonly ILogger<DocumentLayoutService> _logger;

    public DocumentLayoutService(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IConfiguration config,
        ILogger<DocumentLayoutService> logger)
    {
        _http = httpClientFactory.CreateClient(nameof(DocumentLayoutService));
        _credential = credential;
        _logger = logger;
        _endpoint = (config["Azure:DocumentIntelligence:Endpoint"] ?? string.Empty).TrimEnd('/');
    }

    public async Task<ExtractedDocument> AnalyzeAsync(BinaryData pdf, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_endpoint))
            throw new ArgumentException(
                "PDF support requires Azure Document Intelligence to be configured — see README.");

        var operationLocation = await SubmitAsync(pdf, ct);
        var analyzeResult = await PollAsync(operationLocation, ct);
        return Map(analyzeResult);
    }

    private async Task<string> SubmitAsync(BinaryData pdf, CancellationToken ct)
    {
        var url = $"{_endpoint}/documentintelligence/documentModels/prebuilt-read:analyze" +
                  $"?api-version={ApiVersion}&stringIndexType=utf16CodeUnit&outputContentFormat=text";

        var payload = new { base64Source = Convert.ToBase64String(pdf.ToArray()) };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetBearerTokenAsync(ct));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Document Intelligence returned {(int)response.StatusCode} on submit: {err}");
        }

        if (!response.Headers.TryGetValues("Operation-Location", out var locations))
            throw new InvalidOperationException("Document Intelligence did not return an Operation-Location header.");

        return locations.First();
    }

    private async Task<JsonElement> PollAsync(string operationLocation, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        const int maxAttempts = 120;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await Task.Delay(delay, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetBearerTokenAsync(ct));

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Document Intelligence returned {(int)response.StatusCode} while polling: {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            switch (status)
            {
                case "succeeded":
                    // Re-parse into a detached clone so the JsonDocument can be disposed safely.
                    return root.GetProperty("analyzeResult").Clone();
                case "failed":
                    throw new InvalidOperationException($"Document Intelligence analysis failed: {body}");
            }

            if (delay < TimeSpan.FromSeconds(4))
                delay *= 2;
        }

        throw new TimeoutException("Document Intelligence analysis did not complete in time.");
    }

    private static ExtractedDocument Map(JsonElement analyzeResult)
    {
        var content = analyzeResult.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;

        var pages = new List<PageInfo>();
        var words = new List<LayoutWord>();

        if (analyzeResult.TryGetProperty("pages", out var pagesEl))
        {
            foreach (var page in pagesEl.EnumerateArray())
            {
                var pageNumber = page.TryGetProperty("pageNumber", out var pn) ? pn.GetInt32() : pages.Count + 1;
                var width = page.TryGetProperty("width", out var w) ? w.GetDouble() : 0;
                var height = page.TryGetProperty("height", out var h) ? h.GetDouble() : 0;
                pages.Add(new PageInfo { Page = pageNumber, Width = width, Height = height });

                if (width <= 0 || height <= 0 || !page.TryGetProperty("words", out var wordsEl))
                    continue;

                foreach (var word in wordsEl.EnumerateArray())
                {
                    if (!word.TryGetProperty("span", out var span) || !word.TryGetProperty("polygon", out var poly))
                        continue;

                    var offset = span.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                    var length = span.TryGetProperty("length", out var l) ? l.GetInt32() : 0;

                    var box = PolygonToNormalizedBox(poly, pageNumber, width, height);
                    if (box is not null)
                        words.Add(new LayoutWord(offset, length, box));
                }
            }
        }

        return new ExtractedDocument(content, pages, words);
    }

    private static DetectedBox? PolygonToNormalizedBox(JsonElement polygon, int page, double pageWidth, double pageHeight)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var count = 0;

        var values = polygon.EnumerateArray().ToArray();
        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            var x = values[i].GetDouble();
            var y = values[i + 1].GetDouble();
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            count++;
        }

        if (count == 0)
            return null;

        return new DetectedBox
        {
            Page = page,
            X = Clamp01(minX / pageWidth),
            Y = Clamp01(minY / pageHeight),
            Width = Clamp01((maxX - minX) / pageWidth),
            Height = Clamp01((maxY - minY) / pageHeight)
        };
    }

    private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));

    private async Task<string> GetBearerTokenAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(CognitiveServicesScopes), ct);
        return token.Token;
    }
}
