using DocumentRedaction.API.Models;
using PDFtoImage;
using SkiaSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DocumentRedaction.API.Services;

/// <summary>
/// Handles PDF documents. Text and per-word coordinates come from Azure Document
/// Intelligence; redaction rasterizes each page, paints opaque bars over the selected
/// words, and rebuilds an image-based PDF. Rasterizing guarantees the underlying text is
/// gone (no copy-paste leak) — the trade-off is the redacted PDF's text is no longer
/// selectable, which is the desired behavior for a released, de-identified document.
/// </summary>
public sealed class PdfDocumentProcessor : IDocumentFormatProcessor
{
    private const int RenderDpi = 200;

    private readonly IDocumentLayoutService _layout;
    private readonly ILogger<PdfDocumentProcessor> _logger;

    public PdfDocumentProcessor(IDocumentLayoutService layout, ILogger<PdfDocumentProcessor> logger)
    {
        _layout = layout;
        _logger = logger;
    }

    public bool CanHandle(string contentType) =>
        contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(BinaryData content, CancellationToken ct = default) =>
        _layout.AnalyzeAsync(content, ct);

    public Task<BinaryData> RedactAsync(
        BinaryData original,
        IReadOnlyList<DetectedEntity> selectedEntities,
        CancellationToken ct = default)
    {
        // Group the selected entities' page boxes by page. Page geometry is derived from the
        // rendered bitmap and DPI, so no second Document Intelligence call is needed.
        var boxesByPage = new Dictionary<int, List<DetectedBox>>();
        foreach (var entity in selectedEntities)
        {
            foreach (var box in entity.Boxes)
            {
                if (!boxesByPage.TryGetValue(box.Page, out var list))
                    boxesByPage[box.Page] = list = new List<DetectedBox>();
                list.Add(box);
            }
        }

        var pdfBytes = original.ToArray();

        using var output = new PdfDocument();

        var pageIndex = 0;
        foreach (var bitmap in Conversion.ToImages(pdfBytes, options: new RenderOptions(Dpi: RenderDpi)))
        {
            ct.ThrowIfCancellationRequested();
            using (bitmap)
            {
                var pageNumber = pageIndex + 1;

                if (boxesByPage.TryGetValue(pageNumber, out var boxes) && boxes.Count > 0)
                    PaintBars(bitmap, boxes);

                AppendImagePage(output, bitmap);
                pageIndex++;
            }
        }

        using var resultStream = new MemoryStream();
        output.Save(resultStream);
        return Task.FromResult(BinaryData.FromBytes(resultStream.ToArray()));
    }

    private static void PaintBars(SKBitmap bitmap, IReadOnlyList<DetectedBox> boxes)
    {
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };

        // Small padding so anti-aliased glyph edges are fully covered.
        const float pad = 2f;

        foreach (var box in boxes)
        {
            var x = (float)(box.X * bitmap.Width) - pad;
            var y = (float)(box.Y * bitmap.Height) - pad;
            var w = (float)(box.Width * bitmap.Width) + pad * 2;
            var h = (float)(box.Height * bitmap.Height) + pad * 2;
            canvas.DrawRect(SKRect.Create(x, y, w, h), paint);
        }

        canvas.Flush();
    }

    private static void AppendImagePage(PdfDocument output, SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        // Page size in inches = rendered pixels / DPI.
        var widthInch = (double)bitmap.Width / RenderDpi;
        var heightInch = (double)bitmap.Height / RenderDpi;

        var page = output.AddPage();
        page.Width = XUnit.FromInch(widthInch);
        page.Height = XUnit.FromInch(heightInch);

        using var gfx = XGraphics.FromPdfPage(page);
        using var pngStream = new MemoryStream(data.ToArray());
        using var xImage = XImage.FromStream(pngStream);
        gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
    }
}
