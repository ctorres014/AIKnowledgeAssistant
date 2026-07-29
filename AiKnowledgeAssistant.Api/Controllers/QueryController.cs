using System.Text.Json.Serialization;
using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Domain.Rag;
using Microsoft.AspNetCore.Mvc;

namespace AiKnowledgeAssistant.Api.Controllers;

/// <summary>
/// Query endpoint (SPEC 03). Answers a natural-language question strictly from the indexed corpus:
/// when nothing retrieved clears the score threshold the response is a plain
/// <c>foundAnswer: false</c>, not an invented answer.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class QueryController : ControllerBase
{
    private readonly KnowledgeOrchestrator _orchestrator;

    /// <remarks>
    /// The orchestrator is the use case's only entry point; the controller never sees
    /// <c>RagPipeline</c>, so SPEC 04 can put the semantic cache in front of it without touching
    /// this class or its tests.
    /// </remarks>
    public QueryController(KnowledgeOrchestrator orchestrator) => _orchestrator = orchestrator;

    /// <summary>Answers a question from the indexed documents.</summary>
    // POST /api/query
    [HttpPost(Name = "Query")]
    [ProducesResponseType<QueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<QueryErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<QueryErrorResponse>(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Post([FromBody] QueryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            ModelState.AddModelError(nameof(QueryRequest.Question), "A question is required.");
            return ValidationProblem(ModelState);
        }

        var result = await _orchestrator.AskAsync(request.Question, ct);

        if (result.IsSuccess)
        {
            return Ok(QueryResponse.From(result.Value!, request.IncludeChunks));
        }

        return StatusCode(StatusFor(result.ErrorCode), new QueryErrorResponse(result.ErrorCode ?? "QueryFailed"));
    }

    /// <summary>
    /// A dependency being down or slow is an expected outcome with its own status code, so the client
    /// can tell "retry in a moment" from "this took too long" without parsing prose.
    /// </summary>
    private static int StatusFor(string? errorCode) => errorCode switch
    {
        "LlmTimeout" => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status503ServiceUnavailable
    };
}

/// <summary>Request contract for POST /api/query.</summary>
/// <param name="Question">The natural-language question. Required.</param>
/// <param name="IncludeChunks">
/// Debugging aid: when true every citation also carries the chunk text that grounded the answer.
/// Off by default, so responses stay light and an endpoint that has no authentication yet does not
/// hand out the full contents of internal documents.
/// </param>
public sealed record QueryRequest(string Question, bool IncludeChunks = false);

/// <summary>Response contract for POST /api/query.</summary>
public sealed record QueryResponse(
    string? Answer,
    bool FoundAnswer,
    string Model,
    long DurationMs,
    IReadOnlyList<CitationResponse> Citations)
{
    /// <summary>
    /// The pipeline always carries the chunk text; whether it reaches the client is a presentation
    /// decision taken here, which keeps one execution path behind both response shapes.
    /// </summary>
    public static QueryResponse From(Answer answer, bool includeChunks) => new(
        answer.Text,
        answer.FoundAnswer,
        answer.Model,
        answer.DurationMs,
        [.. answer.Citations.Select(c => CitationResponse.From(c, includeChunks))]);
}

/// <summary>One document fragment that grounded the answer.</summary>
public sealed record CitationResponse(
    string Title,
    string SourceId,
    string SourceType,
    int ChunkIndex,
    float Score,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text)
{
    public static CitationResponse From(RetrievedChunk chunk, bool includeChunks) => new(
        chunk.Title,
        chunk.SourceId,
        chunk.SourceType.ToString(),
        chunk.ChunkIndex,
        chunk.Score,
        includeChunks ? chunk.Text : null);
}

/// <summary>Body returned when a dependency of the query path fails.</summary>
public sealed record QueryErrorResponse(string Error);
