using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.Api.Controllers;

/// <summary>
/// Ingestion endpoints (SPEC 02). Ingestion is synchronous and bounded: an oversized batch is
/// rejected up front, and a run that exhausts its time budget returns a partial summary rather
/// than dying on the connection. Retrying the same request resumes where it stopped.
/// </summary>
[ApiController]
[Route("api/ingest")]
public sealed class IngestController : ControllerBase
{
    private readonly IngestDocumentsHandler _handler;
    private readonly IVectorStore _vectorStore;
    private readonly IngestionOptions _options;

    public IngestController(
        IngestDocumentsHandler handler,
        IVectorStore vectorStore,
        IOptions<IngestionOptions> options)
    {
        _handler = handler;
        _vectorStore = vectorStore;
        _options = options.Value;
    }

    /// <summary>Ingests a local file or folder.</summary>
    // POST /api/ingest
    [HttpPost(Name = "Ingest")]
    [ProducesResponseType<IngestionSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Post([FromBody] IngestRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            ModelState.AddModelError(nameof(IngestRequest.Path), "A file or folder path is required.");
            return ValidationProblem(ModelState);
        }

        var result = await _handler.HandleAsync(
            new IngestDocumentsCommand(request.Path, request.SourceType), ct);

        if (result.IsSuccess)
        {
            return Ok(IngestionSummaryResponse.From(result.Value!));
        }

        // The file cap has its own body shape so a client can adapt its batch size.
        if (result.ErrorCode == TooManyFiles)
        {
            return BadRequest(new TooManyFilesResponse(TooManyFiles, _options.MaxFilesPerRequest));
        }

        ModelState.AddModelError(FieldFor(result.ErrorCode), result.Error!);
        return ValidationProblem(ModelState);
    }

    /// <summary>Collection name, vector count and dimension of the index.</summary>
    // GET /api/ingest/stats
    [HttpGet("stats", Name = "IngestStats")]
    [ProducesResponseType<VectorStoreStats>(StatusCodes.Status200OK)]
    public async Task<ActionResult<VectorStoreStats>> GetStats(CancellationToken ct) =>
        Ok(await _vectorStore.GetStatsAsync(ct));

    private const string TooManyFiles = "TooManyFiles";

    /// <summary>Attributes a failure to the request field that caused it.</summary>
    private static string FieldFor(string? errorCode) => errorCode switch
    {
        "UnknownSourceType" => nameof(IngestRequest.SourceType),
        _ => nameof(IngestRequest.Path)
    };
}

/// <summary>Request contract for POST /api/ingest.</summary>
/// <param name="Path">A file, or a folder walked recursively.</param>
/// <param name="SourceType">
/// Optional: <c>pdf</c>, <c>txt</c> or <c>markdown</c>. Omit it to infer the source per file
/// from its extension; supply it to ingest only the files of that type.
/// </param>
public sealed record IngestRequest(string Path, string? SourceType);

/// <summary>Response contract for POST /api/ingest.</summary>
public sealed record IngestionSummaryResponse(
    string Status,
    int TotalFiles,
    int Ingested,
    int Skipped,
    int FailedCount,
    int Remaining,
    int ChunksIndexed,
    long DurationMs,
    IReadOnlyList<IngestionFailureResponse> Failed)
{
    public static IngestionSummaryResponse From(IngestionSummary summary) => new(
        summary.Status.ToString(),
        summary.TotalFiles,
        summary.Ingested,
        summary.Skipped,
        summary.FailedCount,
        summary.Remaining,
        summary.ChunksIndexed,
        summary.DurationMs,
        [.. summary.Failed.Select(f => new IngestionFailureResponse(f.Path, f.Reason))]);
}

/// <summary>One file that could not be ingested.</summary>
public sealed record IngestionFailureResponse(string Path, string Reason);

/// <summary>Body returned when the batch exceeds the per-request file cap.</summary>
public sealed record TooManyFilesResponse(string Error, int MaxFilesPerRequest);
