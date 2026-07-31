using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Handles Word (.docx) documents. Text is extracted by concatenating the runs in
/// document order (one line per paragraph); redaction masks the exact characters inside
/// the affected runs via the OpenXML SDK, preserving all document formatting. The text
/// built during extraction and during redaction is byte-for-byte identical, so PII
/// offsets line up on both passes.
/// </summary>
public sealed class DocxDocumentProcessor : IDocumentFormatProcessor
{
    private const char MaskChar = '\u2588'; // █

    public const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public bool CanHandle(string contentType) =>
        contentType.Equals(DocxContentType, StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(BinaryData content, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(content.ToArray());
        using var doc = WordprocessingDocument.Open(stream, false);

        var (text, _) = BuildText(doc);
        return Task.FromResult(new ExtractedDocument(text, Array.Empty<Models.PageInfo>(), Array.Empty<LayoutWord>()));
    }

    public Task<BinaryData> RedactAsync(
        BinaryData original,
        IReadOnlyList<Models.DetectedEntity> selectedEntities,
        CancellationToken ct = default)
    {
        // Work on a writable copy of the original bytes.
        var bytes = original.ToArray();
        using var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;

        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            var (_, runs) = BuildText(doc);

            foreach (var run in runs)
            {
                var runStart = run.Start;
                var runEnd = run.Start + run.Length;
                var chars = run.Element.Text.ToCharArray();
                var modified = false;

                foreach (var entity in selectedEntities)
                {
                    var spanEnd = entity.Offset + entity.Length;
                    var overlapStart = Math.Max(runStart, entity.Offset);
                    var overlapEnd = Math.Min(runEnd, spanEnd);
                    if (overlapEnd <= overlapStart) continue;

                    for (var i = overlapStart - runStart; i < overlapEnd - runStart; i++)
                    {
                        if (!char.IsWhiteSpace(chars[i]))
                        {
                            chars[i] = MaskChar;
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    run.Element.Text = new string(chars);
                    run.Element.Space = SpaceProcessingModeValues.Preserve;
                }
            }

            doc.MainDocumentPart!.Document.Save();
        }

        return Task.FromResult(BinaryData.FromBytes(stream.ToArray()));
    }

    /// <summary>A run's text element with its character range in the concatenated document text.</summary>
    private sealed record TextRun(int Start, int Length, Text Element);

    /// <summary>
    /// Builds the concatenated document text (one line per paragraph) and a map of every
    /// <see cref="Text"/> element to its character range. Deterministic, so extraction and
    /// redaction produce identical offsets.
    /// </summary>
    private static (string Text, List<TextRun> Runs) BuildText(WordprocessingDocument doc)
    {
        var sb = new StringBuilder();
        var runs = new List<TextRun>();

        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return (string.Empty, runs);

        var paragraphs = body.Descendants<Paragraph>().ToList();
        for (var p = 0; p < paragraphs.Count; p++)
        {
            foreach (var textEl in paragraphs[p].Descendants<Text>())
            {
                var value = textEl.Text ?? string.Empty;
                runs.Add(new TextRun(sb.Length, value.Length, textEl));
                sb.Append(value);
            }

            // Separate paragraphs so multi-paragraph entities never merge across a boundary.
            if (p < paragraphs.Count - 1)
                sb.Append('\n');
        }

        return (sb.ToString(), runs);
    }
}
