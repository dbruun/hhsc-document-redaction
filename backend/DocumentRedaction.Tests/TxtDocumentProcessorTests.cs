using System.Text;
using DocumentRedaction.API.Models;
using DocumentRedaction.API.Services;

namespace DocumentRedaction.Tests;

public class TxtDocumentProcessorTests
{
    private readonly TxtDocumentProcessor _sut = new();

    [Theory]
    [InlineData("text/plain", true)]
    [InlineData("TEXT/PLAIN", true)]
    [InlineData("application/pdf", false)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    public void CanHandle_matches_only_plain_text(string contentType, bool expected)
    {
        Assert.Equal(expected, _sut.CanHandle(contentType));
    }

    [Fact]
    public async Task ExtractAsync_returns_decoded_text_and_no_geometry()
    {
        var content = BinaryData.FromBytes(Encoding.UTF8.GetBytes("Patient Jane Doe"));

        var result = await _sut.ExtractAsync(content);

        Assert.Equal("Patient Jane Doe", result.Text);
        Assert.Empty(result.Pages);
        Assert.Empty(result.Words);
    }

    [Fact]
    public async Task RedactAsync_masks_only_selected_spans()
    {
        const string text = "Nurse Amy and patient Bob";
        var original = BinaryData.FromBytes(Encoding.UTF8.GetBytes(text));

        // Redact "Amy" (offset 6, len 3) but keep "Bob".
        var selected = new[] { Entity("e0", "Amy", 6, 3) };

        var redacted = await _sut.RedactAsync(original, selected);
        var output = Encoding.UTF8.GetString(redacted.ToArray());

        Assert.Equal("Nurse \u2588\u2588\u2588 and patient Bob", output);
        Assert.Contains("Bob", output);
        Assert.DoesNotContain("Amy", output);
    }

    [Fact]
    public async Task RedactAsync_preserves_whitespace_inside_a_span()
    {
        const string text = "John Smith";
        var original = BinaryData.FromBytes(Encoding.UTF8.GetBytes(text));

        // Whole "John Smith" as one span — the interior space must survive.
        var redacted = await _sut.RedactAsync(original, new[] { Entity("e0", text, 0, text.Length) });
        var output = Encoding.UTF8.GetString(redacted.ToArray());

        Assert.Equal("\u2588\u2588\u2588\u2588 \u2588\u2588\u2588\u2588\u2588", output);
    }

    [Fact]
    public async Task RedactAsync_with_no_selection_returns_text_unchanged()
    {
        const string text = "Nothing to redact here";
        var original = BinaryData.FromBytes(Encoding.UTF8.GetBytes(text));

        var redacted = await _sut.RedactAsync(original, Array.Empty<DetectedEntity>());

        Assert.Equal(text, Encoding.UTF8.GetString(redacted.ToArray()));
    }

    [Fact]
    public async Task RedactAsync_clamps_spans_that_exceed_text_length()
    {
        const string text = "abc";
        var original = BinaryData.FromBytes(Encoding.UTF8.GetBytes(text));

        // Offset/length deliberately run past the end of the text.
        var redacted = await _sut.RedactAsync(original, new[] { Entity("e0", "abc", 1, 999) });
        var output = Encoding.UTF8.GetString(redacted.ToArray());

        Assert.Equal("a\u2588\u2588", output);
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
