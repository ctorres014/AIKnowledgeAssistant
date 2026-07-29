using System.Diagnostics;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Rag;
using Microsoft.Extensions.Logging;

namespace AiKnowledgeAssistant.Application.Rag;

/// <summary>
/// The central component of the PRD's architecture (§7): semantic cache → RAG → persistence.
/// </summary>
/// <remarks>
/// <para>
/// The <em>only</em> entry point of the query use case. <see cref="RagPipeline"/> is an internal
/// detail the API never sees, which is what lets SPEC 04 slot the cache in front of it without
/// touching the controller or its tests.
/// </para>
/// <para>
/// Two of the three stages are seams rather than behaviour in this spec: the cache lookup
/// (<see cref="TryGetCachedAnswerAsync"/>, SPEC 04) and the persistence of the question/answer pair
/// (<see cref="PersistAsync"/>, SPEC 05). They are declared as methods, in the order the PRD puts
/// them, so filling them in is a change of body and not a change of shape.
/// </para>
/// </remarks>
public sealed class KnowledgeOrchestrator
{
    private readonly RagPipeline _pipeline;
    private readonly ILogger<KnowledgeOrchestrator> _logger;

    public KnowledgeOrchestrator(RagPipeline pipeline, ILogger<KnowledgeOrchestrator> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <summary>Answers a natural-language question, or reports why it could not be answered.</summary>
    public async Task<Result<Answer>> AskAsync(string question, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        // The root of the query trace: the use case starts here, so a cache hit in SPEC 04 will show
        // up under the same span as the RAG path it replaces.
        using var span = RagTelemetry.ActivitySource.StartActivity(RagTelemetry.QuerySpan);
        var stopwatch = Stopwatch.StartNew();

        var cached = await TryGetCachedAnswerAsync(question, ct);
        if (cached is not null)
        {
            return Result<Answer>.Success(cached);
        }

        var answer = await _pipeline.AnswerAsync(question, ct);

        RagTelemetry.RecordStage(RagTelemetry.TotalStage, stopwatch.Elapsed);

        if (!answer.IsSuccess)
        {
            _logger.LogWarning("Query failed with {ErrorCode}: {Error}", answer.ErrorCode, answer.Error);

            RagTelemetry.QueriesFailed.Add(1);
            span?.SetTag("rag.error_code", answer.ErrorCode);
            span?.SetStatus(ActivityStatusCode.Error, answer.Error);

            return answer;
        }

        Record(span, answer.Value!);

        await PersistAsync(question, answer.Value!, ct);

        return answer;
    }

    /// <summary>Tags the query span with the outcome and counts it as answered or unanswered.</summary>
    private static void Record(Activity? span, Answer answer)
    {
        span?.SetTag("rag.model", answer.Model);
        span?.SetTag("rag.found_answer", answer.FoundAnswer);
        span?.SetTag("rag.citations", answer.Citations.Count);
        span?.SetTag("rag.duration_ms", answer.DurationMs);

        if (answer.FoundAnswer)
        {
            RagTelemetry.QueriesAnswered.Add(1);
        }
        else
        {
            RagTelemetry.QueriesWithoutResults.Add(1);
        }
    }

    /// <summary>
    /// Cache-before-RAG is core to the PRD's performance targets, but there is no cache of any kind
    /// until SPEC 04 — this always misses, so every question reaches the pipeline.
    /// </summary>
    private Task<Answer?> TryGetCachedAnswerAsync(string question, CancellationToken ct) =>
        Task.FromResult<Answer?>(null);

    /// <summary>
    /// Persistence of the question/answer pair, feedback and audit trail (FR-004/006/007) arrives with
    /// SPEC 05, together with the relational store. Until then a query leaves no trace beyond its
    /// OpenTelemetry spans.
    /// </summary>
    private Task PersistAsync(string question, Answer answer, CancellationToken ct) => Task.CompletedTask;
}
