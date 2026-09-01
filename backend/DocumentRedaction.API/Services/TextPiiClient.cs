using Azure.Core;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Detects PII in plain text via the Azure Language synchronous text endpoint
/// (<c>/language/:analyze-text</c>, kind <c>PiiEntityRecognition</c>). Unlike the
/// native-document endpoint, this returns each entity with a character offset, which
/// is what enables per-instance selection and highlighting.
/// </summary>
public interface ITextPiiClient
{
    /// <summary>
    /// Returns every detected PII instance in <paramref name="text"/>, with offsets in
    /// UTF-16 code units (so they index the string directly).
    /// </summary>
    Task<IReadOnlyList<PiiEntity>> DetectAsync(string text, CancellationToken ct = default);
}

public sealed class TextPiiClient : ITextPiiClient
{
    private const string ApiVersion = "2024-11-15-preview";

    /// The synchronous endpoint accepts at most 5,120 characters per document.
    private const int MaxChunkChars = 5_000;
    private const int ChunkOverlapChars = 250;

    private static readonly string[] CognitiveServicesScopes = { "https://cognitiveservices.azure.com/.default" };

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly string _endpoint;
    private readonly ILogger<TextPiiClient> _logger;

    public TextPiiClient(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IConfiguration config,
        ILogger<TextPiiClient> logger)
    {
        _http = httpClientFactory.CreateClient(nameof(TextPiiClient));
        _credential = credential;
        _logger = logger;
        _endpoint = (config["Azure:Language:Endpoint"]
            ?? throw new InvalidOperationException("Azure:Language:Endpoint is required."))
            .TrimEnd('/');
    }

    public async Task<IReadOnlyList<PiiEntity>> DetectAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<PiiEntity>();

        var results = new List<PiiEntity>();

        foreach (var (chunk, baseOffset) in Chunk(text))
        {
            var entities = await DetectChunkAsync(chunk, ct);
            foreach (var e in entities)
                results.Add(e with { Offset = e.Offset + baseOffset });
        }

        return results
            .GroupBy(entity => (entity.Offset, entity.Length, entity.Category, entity.SubCategory))
            .Select(group => group.OrderByDescending(entity => entity.ConfidenceScore).First())
            .OrderBy(entity => entity.Offset)
            .ThenByDescending(entity => entity.Length)
            .ToList();
    }

    private async Task<IReadOnlyList<PiiEntity>> DetectChunkAsync(string text, CancellationToken ct)
    {
        var payload = new
        {
            kind = "PiiEntityRecognition",
            parameters = new
            {
                modelVersion = "latest",
                stringIndexType = "Utf16CodeUnit"
            },
            analysisInput = new
            {
                documents = new[]
                {
                    new { id = "1", language = "en", text }
                }
            }
        };

        var url = $"{_endpoint}/language/:analyze-text?api-version={ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetBearerTokenAsync(ct));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Language text PII returned {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("results", out var resultsEl)
            && resultsEl.TryGetProperty("errors", out var errorsEl)
            && errorsEl.GetArrayLength() > 0)
        {
            throw new InvalidOperationException(
                $"Language PII rejected a document chunk: {errorsEl[0].GetRawText()}");
        }

        if (!root.TryGetProperty("results", out resultsEl)
            || !resultsEl.TryGetProperty("documents", out var docsEl)
            || docsEl.GetArrayLength() == 0)
        {
            return Array.Empty<PiiEntity>();
        }

        var entities = new List<PiiEntity>();
        var docEl = docsEl[0];
        if (docEl.TryGetProperty("entities", out var entitiesEl))
        {
            foreach (var entity in entitiesEl.EnumerateArray())
            {
                entities.Add(new PiiEntity(
                    Text: entity.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                    Category: entity.TryGetProperty("category", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                    SubCategory: entity.TryGetProperty("subcategory", out var sc) ? sc.GetString() : null,
                    ConfidenceScore: entity.TryGetProperty("confidenceScore", out var cs) ? cs.GetDouble() : 0,
                    Offset: entity.TryGetProperty("offset", out var o) ? o.GetInt32() : 0,
                    Length: entity.TryGetProperty("length", out var l) ? l.GetInt32() : 0));
            }
        }

        return entities;
    }

    private static IEnumerable<(string Chunk, int BaseOffset)> Chunk(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return (text, 0);
            yield break;
        }

        var pos = 0;
        while (pos < text.Length)
        {
            var take = Math.Min(MaxChunkChars, text.Length - pos);
            var end = pos + take;

            // Prefer to break at the last newline within the window to avoid splitting entities.
            if (end < text.Length)
            {
                var lastBreak = text.LastIndexOf('\n', end - 1, take);
                if (lastBreak > pos)
                    end = lastBreak + 1;
            }

            yield return (text[pos..end], pos);
            pos = end < text.Length
                ? Math.Max(pos + 1, end - ChunkOverlapChars)
                : end;
        }
    }

    private async Task<string> GetBearerTokenAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(CognitiveServicesScopes), ct);
        return token.Token;
    }
}
