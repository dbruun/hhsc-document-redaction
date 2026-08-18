using System.Text;
using DocumentRedaction.API.Models;
using DocumentRedaction.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocumentRedaction.Tests;

public class DocumentRedactionOrchestratorTests
{
    [Fact]
    public async Task DetectAsync_assigns_sequential_ids_and_persists_state()
    {
        var pii = new FakePiiClient(
            new PiiEntity("Amy", "Person", "Nurse", 0.9, 6, 3),
            new PiiEntity("Bob", "Person", "Patient", 0.8, 22, 3));
        var blob = new FakeBlobStorage();
        var sut = BuildSut(new FakeProcessor("text/plain"), pii, blob);

        var file = MakeFile("Nurse Amy and patient Bob", "notes.txt", "text/plain");
        var result = await sut.DetectAsync(file);

        Assert.Equal(new[] { "e0", "e1" }, result.Entities.Select(e => e.Id));
        Assert.Equal("Amy", result.Entities[0].Text);
        Assert.Equal("Nurse", result.Entities[0].SubCategory);
        // State + original were stored under the returned job id.
        Assert.True(blob.State.ContainsKey(result.JobId));
        Assert.True(blob.Originals.ContainsKey(result.JobId));
    }

    [Fact]
    public async Task DetectAsync_maps_pdf_word_boxes_into_a_union_box_per_entity()
    {
        // "Dr Smith": two words with boxes that should union into one entity box.
        var extracted = new ExtractedDocument(
            Text: "Dr Smith",
            Pages: new[] { new PageInfo { Page = 1, Width = 8.5, Height = 11 } },
            Words: new[]
            {
                new LayoutWord(0, 2, Box(1, 0.10, 0.10, 0.05, 0.02)), // "Dr"
                new LayoutWord(3, 5, Box(1, 0.16, 0.10, 0.08, 0.02)), // "Smith"
            });
        var processor = new FakeProcessor("application/pdf", extracted);
        var pii = new FakePiiClient(new PiiEntity("Dr Smith", "Person", null, 0.95, 0, 8));
        var sut = BuildSut(processor, pii, new FakeBlobStorage());

        var result = await sut.DetectAsync(MakeFile("%PDF-1.4", "scan.pdf", "application/pdf"));

        var box = Assert.Single(result.Entities[0].Boxes);
        Assert.Equal(1, box.Page);
        Assert.Equal(0.10, box.X, 3);
        Assert.Equal(0.10, box.Y, 3);
        Assert.Equal(0.14, box.Width, 3);   // 0.24 (right of Smith) - 0.10
        Assert.Equal(0.02, box.Height, 3);
    }

    [Fact]
    public async Task ApplyAsync_redacts_only_the_selected_instances()
    {
        var pii = new FakePiiClient(
            new PiiEntity("Amy", "Person", "Nurse", 0.9, 0, 3),
            new PiiEntity("Cara", "Person", "Doctor", 0.9, 5, 4),
            new PiiEntity("Bob", "Person", "Patient", 0.9, 14, 3));
        var extracted = new ExtractedDocument(
            "Amy, Cara and Bob",
            Array.Empty<PageInfo>(),
            Array.Empty<LayoutWord>());
        var blob = new FakeBlobStorage();
        var processor = new FakeProcessor("text/plain", extracted);
        var sut = BuildSut(processor, pii, blob);

        var detection = await sut.DetectAsync(MakeFile(extracted.Text, "n.txt", "text/plain"));

        // Redact the nurse and doctor, keep the patient.
        var apply = await sut.ApplyAsync(detection.JobId, new[] { "e0", "e1" });

        Assert.Equal(2, apply.RedactedCount);
        Assert.Equal(new[] { "Amy", "Cara" }, processor.LastSelected!.Select(e => e.Text));
        Assert.DoesNotContain("Bob", processor.LastSelected!.Select(e => e.Text));
    }

    [Fact]
    public async Task ApplyAsync_redacts_manual_terms_everywhere()
    {
        var extracted = new ExtractedDocument(
            "Amy met Bob. Later amy called Cara.",
            Array.Empty<PageInfo>(),
            Array.Empty<LayoutWord>());
        var blob = new FakeBlobStorage();
        var processor = new FakeProcessor("text/plain", extracted);
        var sut = BuildSut(processor, new FakePiiClient(), blob);

        var detection = await sut.DetectAsync(
            MakeFile(extracted.Text, "n.txt", "text/plain"));

        var apply = await sut.ApplyAsync(
            detection.JobId,
            Array.Empty<string>(),
            new[] { "Amy" },
            Array.Empty<string>());

        Assert.Equal(2, apply.RedactedCount);
        Assert.Equal(new[] { 0, 19 }, processor.LastSelected!.Select(e => e.Offset));
        Assert.All(processor.LastSelected!, e => Assert.Equal(3, e.Length));
    }

    [Fact]
    public async Task ApplyAsync_keeps_detected_entity_selection_per_instance()
    {
        var extracted = new ExtractedDocument(
            "Amy met Amy.",
            Array.Empty<PageInfo>(),
            Array.Empty<LayoutWord>());
        var pii = new FakePiiClient(
            new PiiEntity("Amy", "Person", "Nurse", 0.9, 0, 3),
            new PiiEntity("Amy", "Person", "Nurse", 0.9, 8, 3));
        var blob = new FakeBlobStorage();
        var processor = new FakeProcessor("text/plain", extracted);
        var sut = BuildSut(processor, pii, blob);

        var detection = await sut.DetectAsync(MakeFile(extracted.Text, "n.txt", "text/plain"));

        var apply = await sut.ApplyAsync(detection.JobId, new[] { "e0" });

        var selected = Assert.Single(processor.LastSelected!);
        Assert.Equal(0, selected.Offset);
        Assert.Equal(1, apply.RedactedCount);
    }

    [Fact]
    public async Task ApplyAsync_excludes_whitelisted_terms_from_selected_and_manual_redactions()
    {
        var pii = new FakePiiClient(
            new PiiEntity("Amy", "Person", "Nurse", 0.9, 0, 3),
            new PiiEntity("Bob", "Person", "Patient", 0.9, 8, 3));
        var blob = new FakeBlobStorage();
        var processor = new FakeProcessor("text/plain");
        var sut = BuildSut(processor, pii, blob);

        var detection = await sut.DetectAsync(MakeFile("Amy and Bob", "n.txt", "text/plain"));

        var apply = await sut.ApplyAsync(
            detection.JobId,
            new[] { "e0", "e1" },
            new[] { "Amy" },
            new[] { "Amy" });

        var selected = Assert.Single(processor.LastSelected!);
        Assert.Equal("Bob", selected.Text);
        Assert.Equal(1, apply.RedactedCount);
    }

    [Fact]
    public async Task ApplyAsync_throws_for_unknown_job()
    {
        var sut = BuildSut(new FakeProcessor("text/plain"), new FakePiiClient(), new FakeBlobStorage());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.ApplyAsync("does-not-exist", new[] { "e0" }));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("text/html")]
    public async Task DetectAsync_rejects_unsupported_content_types(string contentType)
    {
        var sut = BuildSut(new FakeProcessor("text/plain"), new FakePiiClient(), new FakeBlobStorage());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.DetectAsync(MakeFile("data", "f.bin", contentType)));
    }

    [Fact]
    public async Task DetectAsync_rejects_empty_files()
    {
        var sut = BuildSut(new FakeProcessor("text/plain"), new FakePiiClient(), new FakeBlobStorage());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.DetectAsync(MakeFile("", "empty.txt", "text/plain")));
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static DocumentRedactionOrchestrator BuildSut(
        IDocumentFormatProcessor processor, ITextPiiClient pii, IBlobStorageService blob) =>
        new(new[] { processor }, pii, blob, NullLogger<DocumentRedactionOrchestrator>.Instance);

    private static DetectedBox Box(int page, double x, double y, double w, double h) =>
        new() { Page = page, X = x, Y = y, Width = w, Height = h };

    private static IFormFile MakeFile(string content, string fileName, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var ms = new MemoryStream(bytes);
        return new FormFile(ms, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    // ─── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeProcessor : IDocumentFormatProcessor
    {
        private readonly string _contentType;
        private readonly ExtractedDocument _extracted;

        public IReadOnlyList<DetectedEntity>? LastSelected { get; private set; }

        public FakeProcessor(string contentType, ExtractedDocument? extracted = null)
        {
            _contentType = contentType;
            _extracted = extracted ?? new ExtractedDocument(
                "Nurse Amy and patient Bob", Array.Empty<PageInfo>(), Array.Empty<LayoutWord>());
        }

        public bool CanHandle(string contentType) =>
            contentType.Equals(_contentType, StringComparison.OrdinalIgnoreCase);

        public Task<ExtractedDocument> ExtractAsync(BinaryData content, CancellationToken ct = default) =>
            Task.FromResult(_extracted);

        public Task<BinaryData> RedactAsync(
            BinaryData original, IReadOnlyList<DetectedEntity> selectedEntities, CancellationToken ct = default)
        {
            LastSelected = selectedEntities;
            return Task.FromResult(BinaryData.FromBytes(Encoding.UTF8.GetBytes("REDACTED")));
        }
    }

    private sealed class FakePiiClient : ITextPiiClient
    {
        private readonly IReadOnlyList<PiiEntity> _entities;
        public FakePiiClient(params PiiEntity[] entities) => _entities = entities;

        public Task<IReadOnlyList<PiiEntity>> DetectAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(_entities);
    }

    /// <summary>Minimal in-memory blob store covering only what detect/apply touch.</summary>
    private sealed class FakeBlobStorage : IBlobStorageService
    {
        public Dictionary<string, string> State { get; } = new();
        public Dictionary<string, BlobDownload> Originals { get; } = new();
        public Dictionary<string, BlobDownload> Redacted { get; } = new();

        public Task<string> UploadAsync(Stream content, string blobName, string contentType, CancellationToken ct = default)
        {
            var jobId = Path.GetFileNameWithoutExtension(blobName["original/".Length..]);
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            Originals[jobId] = new BlobDownload(BinaryData.FromBytes(ms.ToArray()), contentType);
            return Task.FromResult($"memory://{blobName}");
        }

        public Task UploadStateAsync(string jobId, string json, CancellationToken ct = default)
        {
            State[jobId] = json;
            return Task.CompletedTask;
        }

        public Task<string?> DownloadStateAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult(State.TryGetValue(jobId, out var v) ? v : null);

        public Task<BlobDownload?> DownloadOriginalAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult(Originals.TryGetValue(jobId, out var v) ? v : null);

        public Task<string> UploadToRedactedAsync(BinaryData content, string blobName, string contentType, CancellationToken ct = default)
        {
            var jobId = Path.GetFileNameWithoutExtension(blobName["redacted/".Length..]);
            Redacted[jobId] = new BlobDownload(content, contentType);
            return Task.FromResult($"memory://{blobName}");
        }

        // Unused by detect/apply.
        public Task<string> UploadTextAsync(string text, string blobName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> GenerateBlobSasUrlAsync(string blobName, Azure.Storage.Sas.BlobSasPermissions p, TimeSpan v, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> GenerateContainerSasUrlAsync(Azure.Storage.Sas.BlobContainerSasPermissions p, TimeSpan v, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> DownloadTextBlobAsync(string blobUrl, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BlobDownload> DownloadDocumentAsync(string blobUrl, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BlobStream?> OpenOriginalDocumentAsync(string jobId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BlobStream?> OpenRedactedDocumentAsync(string jobId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
