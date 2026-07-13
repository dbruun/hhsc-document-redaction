using DocumentRedaction.API.Models;
using DocumentRedaction.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocumentRedaction.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class DocumentController : ControllerBase
{
    private readonly IDocumentRedactionOrchestrator _orchestrator;
    private readonly ILogger<DocumentController> _logger;

    public DocumentController(
        IDocumentRedactionOrchestrator orchestrator,
        ILogger<DocumentController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// DETECT phase. Accepts a document upload, extracts its text, and returns every detected
    /// PII instance (with per-instance page boxes for PDFs) for interactive review. Nothing is
    /// redacted yet.
    /// </summary>
    [HttpPost("detect")]
    [RequestSizeLimit(52_428_800)] // 50 MB
    [ProducesResponseType(typeof(DetectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Detect(IFormFile file, CancellationToken ct)
    {
        _logger.LogInformation(
            "Received detect request for '{FileName}' ({Bytes} bytes)", file?.FileName, file?.Length);

        try
        {
            var result = await _orchestrator.DetectAsync(file!, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error for file '{FileName}'", file?.FileName);
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error detecting PII in '{FileName}'", file?.FileName);
            return Problem("An unexpected error occurred while analyzing the document.", ex);
        }
    }

    /// <summary>
    /// APPLY phase. Redacts only the selected PII instances and returns the redacted document
    /// location. The output preserves the original format.
    /// </summary>
    [HttpPost("{jobId}/apply")]
    [ProducesResponseType(typeof(ApplyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Apply(string jobId, [FromBody] ApplyRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _orchestrator.ApplyAsync(jobId, request.SelectedEntityIds ?? [], ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Apply validation error for job '{JobId}'", jobId);
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error applying redactions for job '{JobId}'", jobId);
            return Problem("An unexpected error occurred while redacting the document.", ex);
        }
    }

    /// <summary>Streams the original uploaded document for a job.</summary>
    [HttpGet("{jobId}/original")]
    public async Task<IActionResult> GetOriginal(string jobId, CancellationToken ct)
    {
        var doc = await _orchestrator.OpenOriginalAsync(jobId, ct);
        if (doc is null) return NotFound();
        return File(doc.Content, doc.ContentType);
    }

    /// <summary>Streams the redacted document for a job.</summary>
    [HttpGet("{jobId}/redacted")]
    public async Task<IActionResult> GetRedacted(string jobId, CancellationToken ct)
    {
        var doc = await _orchestrator.OpenRedactedAsync(jobId, ct);
        if (doc is null) return NotFound();
        return File(doc.Content, doc.ContentType);
    }

    private IActionResult Problem(string message, Exception ex) =>
        StatusCode(
            StatusCodes.Status500InternalServerError,
            new ErrorResponse { Error = message, Details = ex.Message });
}
