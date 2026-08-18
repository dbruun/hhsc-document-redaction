using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentRedaction.API.Models;
using DocumentRedaction.API.Services;

namespace DocumentRedaction.Tests;

public class DocxDocumentProcessorTests
{
    private const string DocxType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const char Mask = '\u2588';

    private readonly DocxDocumentProcessor _sut = new();

    [Theory]
    [InlineData(DocxType, true)]
    [InlineData("text/plain", false)]
    [InlineData("application/pdf", false)]
    public void CanHandle_matches_only_docx(string contentType, bool expected)
    {
        Assert.Equal(expected, _sut.CanHandle(contentType));
    }

    [Fact]
    public async Task ExtractAsync_joins_paragraphs_with_newlines()
    {
        var docx = BuildDocx(
            new[] { "Nurse Amy" },
            new[] { "Patient Bob" });

        var result = await _sut.ExtractAsync(docx);

        Assert.Equal("Nurse Amy\nPatient Bob", result.Text);
    }

    [Fact]
    public async Task ExtractAsync_concatenates_multiple_runs_in_a_paragraph()
    {
        // A paragraph split across two runs: "Nurse " + "Amy".
        var docx = BuildDocx(new[] { "Nurse ", "Amy" });

        var result = await _sut.ExtractAsync(docx);

        Assert.Equal("Nurse Amy", result.Text);
    }

    [Fact]
    public async Task RedactAsync_masks_only_selected_instance_and_keeps_the_rest()
    {
        var docx = BuildDocx(new[] { "Nurse Amy and patient Bob" });
        var extracted = await _sut.ExtractAsync(docx);
        Assert.Equal("Nurse Amy and patient Bob", extracted.Text);

        // Redact "Amy" (offset 6, length 3); keep "Bob".
        var redacted = await _sut.RedactAsync(docx, new[] { Entity("e0", "Amy", 6, 3) });

        var text = ReadDocxText(redacted);
        Assert.Contains($"{Mask}{Mask}{Mask}", text);
        Assert.Contains("Bob", text);
        Assert.DoesNotContain("Amy", text);
    }

    [Fact]
    public async Task RedactAsync_masks_a_span_that_crosses_two_runs()
    {
        // "Nurse " + "Amy" split across runs; redact the whole "Nurse Amy".
        var docx = BuildDocx(new[] { "Nurse ", "Amy" });
        var extracted = await _sut.ExtractAsync(docx);

        var redacted = await _sut.RedactAsync(
            docx, new[] { Entity("e0", "Nurse Amy", 0, extracted.Text.Length) });

        var text = ReadDocxText(redacted);
        Assert.DoesNotContain("Nurse", text);
        Assert.DoesNotContain("Amy", text);
        // Interior space preserved, letters masked.
        Assert.Contains($"{Mask}{Mask}{Mask}{Mask}{Mask} {Mask}{Mask}{Mask}", text);
    }

    [Fact]
    public async Task RedactAsync_with_no_selection_leaves_document_text_intact()
    {
        var docx = BuildDocx(new[] { "Keep everything" });

        var redacted = await _sut.RedactAsync(docx, Array.Empty<DetectedEntity>());

        Assert.Equal("Keep everything", ReadDocxText(redacted));
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Builds a .docx where each argument is a paragraph and each string in it is a run.</summary>
    private static BinaryData BuildDocx(params string[][] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var runs in paragraphs)
            {
                var p = new Paragraph();
                foreach (var runText in runs)
                    p.Append(new Run(new Text(runText) { Space = SpaceProcessingModeValues.Preserve }));
                body.Append(p);
            }
            main.Document = new Document(body);
            main.Document.Save();
        }

        return BinaryData.FromBytes(ms.ToArray());
    }

    private static string ReadDocxText(BinaryData docx)
    {
        using var ms = new MemoryStream(docx.ToArray());
        using var doc = WordprocessingDocument.Open(ms, false);
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>();
        return string.Join("\n", paragraphs.Select(p =>
            string.Concat(p.Descendants<Text>().Select(t => t.Text))));
    }

    private static DetectedEntity Entity(string id, string text, int offset, int length) => new()
    {
        Id = id,
        Text = text,
        Category = "Person",
        SubCategory = null,
        ConfidenceScore = 0.99,
        Offset = offset,
        Length = length,
        Boxes = Array.Empty<DetectedBox>()
    };
}
