using System.Text;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Handles plain-text (.txt) documents. Extraction is the decoded text; redaction masks
/// the selected spans in place with block characters. Fully in-process — no Azure calls
/// beyond the shared PII detection.
/// </summary>
public sealed class TxtDocumentProcessor : IDocumentFormatProcessor
{
    /// <summary>Character used to mask redacted content.</summary>
    public const char MaskChar = '\u2588'; // █

    public bool CanHandle(string contentType) =>
        contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(BinaryData content, CancellationToken ct = default)
    {
        var text = Encoding.UTF8.GetString(content.ToArray());
        return Task.FromResult(new ExtractedDocument(text, Array.Empty<Models.PageInfo>(), Array.Empty<LayoutWord>()));
    }

    public Task<BinaryData> RedactAsync(
        BinaryData original,
        IReadOnlyList<Models.DetectedEntity> selectedEntities,
        CancellationToken ct = default)
    {
        var chars = Encoding.UTF8.GetString(original.ToArray()).ToCharArray();

        foreach (var entity in selectedEntities)
        {
            var start = Math.Max(0, entity.Offset);
            var end = Math.Min(chars.Length, entity.Offset + entity.Length);
            for (var i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(chars[i]))
                    chars[i] = MaskChar;
            }
        }

        var bytes = Encoding.UTF8.GetBytes(new string(chars));
        return Task.FromResult(BinaryData.FromBytes(bytes));
    }
}
