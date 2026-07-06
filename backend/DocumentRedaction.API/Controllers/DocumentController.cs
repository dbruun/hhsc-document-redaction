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
    /// Accepts a document upload, runs the full Azure AI Foundry redaction pipeline,
    /// and returns both the original extracted text and the de-identified version.
    /// </summary>
    /// <param name="file">The document to redact (PDF, DOCX, image, etc.)</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("redact")]
    [RequestSizeLimit(52_428_800)] // 50 MB
    [ProducesResponseType(typeof(RedactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Redact(IFormFile file, CancellationToken ct)
    {
        _logger.LogInformation(
            "Received redaction request for '{FileName}' ({Bytes} bytes)",
            file?.FileName, file?.Length);

        try
        {
            var result = await _orchestrator.ProcessAsync(file!, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error for file '{FileName}'", file?.FileName);
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing file '{FileName}'", file?.FileName);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ErrorResponse
                {
                    Error = "An unexpected error occurred while processing the document.",
                    Details = ex.Message
                });
        }
    }
}
