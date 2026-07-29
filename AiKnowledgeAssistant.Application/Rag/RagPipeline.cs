using System.Diagnostics;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Rag;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.Application.Rag;

/// <summary>
/// The retrieval-augmented generation path: embed the question → search the vector store →
/// build a grounded prompt → generate.
/// </summary>
/// <remarks>
/// <para>
/// An internal detail of <see cref="KnowledgeOrchestrator"/>, which is the use case's only entry
/// point. Nothing outside the orchestrator should take a dependency on this type.
/// </para>
/// <para>
/// When no chunk clears <see cref="RagOptions.MinScore"/> the pipeline answers
/// <c>FoundAnswer: false</c> <em>without calling the LLM</em>: the client can tell "I don't know"
/// from "here you go" without parsing prose, and the most expensive call in the pipeline is skipped
/// to say something the model could not have grounded anyway.
/// </para>
/// </remarks>
public sealed class RagPipeline
{
    private readonly IEmbeddingGenerator _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly ILlmClient _llm;
    private readonly GroundedPromptBuilder _promptBuilder;
    private readonly RagOptions _options;
    private readonly ILogger<RagPipeline> _logger;

    public RagPipeline(
        IEmbeddingGenerator embeddings,
        IVectorStore vectorStore,
        ILlmClient llm,
        GroundedPromptBuilder promptBuilder,
        IOptions<RagOptions> options,
        ILogger<RagPipeline> logger)
    {
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _llm = llm;
        _promptBuilder = promptBuilder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<Answer>> AnswerAsync(string question, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var stopwatch = Stopwatch.StartNew();

        var vector = await EmbedAsync(question, ct);
        if (!vector.IsSuccess)
        {
            return Result<Answer>.Failure(vector.Error!, vector.ErrorCode);
        }

        var search = await _vectorStore.SearchAsync(
            vector.Value!, _options.TopK, _options.MinScore, ct);

        if (!search.IsSuccess)
        {
            return Result<Answer>.Failure(search.Error!, search.ErrorCode);
        }

        var chunks = search.Value!;

        if (chunks.Count == 0)
        {
            _logger.LogInformation(
                "No chunk scored at or above {MinScore}; answering without calling the model",
                _options.MinScore);

            return Result<Answer>.Success(
                new Answer(null, FoundAnswer: false, [], _llm.Model, stopwatch.ElapsedMilliseconds));
        }

        var completion = await _llm.CompleteAsync(_promptBuilder.Build(question, chunks), ct);
        if (!completion.IsSuccess)
        {
            return Result<Answer>.Failure(completion.Error!, completion.ErrorCode);
        }

        return Result<Answer>.Success(new Answer(
            completion.Value!.Text,
            FoundAnswer: true,
            chunks,
            _llm.Model,
            stopwatch.ElapsedMilliseconds));
    }

    /// <summary>
    /// One vector for one question, reusing the ingestion embedder so query and chunks live in the
    /// same vector space.
    /// </summary>
    private async Task<Result<float[]>> EmbedAsync(string question, CancellationToken ct)
    {
        var embedded = await _embeddings.GenerateAsync([question], ct);

        if (!embedded.IsSuccess)
        {
            return Result<float[]>.Failure(embedded.Error!, embedded.ErrorCode);
        }

        var vectors = embedded.Value!;

        if (vectors.Count == 0)
        {
            return Result<float[]>.Failure(
                "EmbeddingRequestFailed: the provider returned no vector for the question.",
                "EmbeddingRequestFailed");
        }

        return Result<float[]>.Success(vectors[0]);
    }
}
