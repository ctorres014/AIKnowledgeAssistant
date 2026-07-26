using System.Diagnostics;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>
/// The ingestion use case: discover → extract → hash → chunk → embed → upsert.
/// </summary>
/// <remarks>
/// <para>
/// Best-effort by design. A file that cannot be read or embedded is recorded in
/// <see cref="IngestionSummary.Failed"/> and the run continues — one corrupt PDF must not block the
/// other forty-nine documents. Idempotency comes from the content hash: a document already indexed
/// with the same hash is skipped without regenerating its embeddings.
/// </para>
/// <para>
/// The run is bounded twice over: a batch above <see cref="IngestionOptions.MaxFilesPerRequest"/> is
/// rejected before any work starts, and <see cref="IngestionOptions.TimeoutSeconds"/> stops the loop
/// <em>between</em> documents so no document is ever left half-indexed. Retrying the same request
/// resumes, because everything already stored counts as skipped.
/// </para>
/// </remarks>
public sealed class IngestDocumentsHandler
{
    private readonly IDocumentLocator _locator;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingGenerator _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly IngestionOptions _options;
    private readonly ILogger<IngestDocumentsHandler> _logger;

    public IngestDocumentsHandler(
        IDocumentLocator locator,
        ITextChunker chunker,
        IEmbeddingGenerator embeddings,
        IVectorStore vectorStore,
        IOptions<IngestionOptions> options,
        ILogger<IngestDocumentsHandler> logger)
    {
        _locator = locator;
        _chunker = chunker;
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<IngestionSummary>> HandleAsync(IngestDocumentsCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var located = _locator.Locate(command.Path, command.SourceType);
        if (!located.IsSuccess)
        {
            return Result<IngestionSummary>.Failure(located.Error!, located.ErrorCode);
        }

        var documents = located.Value!;

        if (documents.Count > _options.MaxFilesPerRequest)
        {
            // Rejected before touching the vector store: nothing is indexed for an oversized batch.
            return Result<IngestionSummary>.Failure(
                $"TooManyFiles: {documents.Count} files exceed the limit of {_options.MaxFilesPerRequest} " +
                "per request. Ingest a narrower path.",
                "TooManyFiles");
        }

        var stopwatch = Stopwatch.StartNew();

        // Deadline for the whole run. Document work keeps using the caller's token so a cut can only
        // land between documents, never halfway through one.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var ingested = 0;
        var skipped = 0;
        var chunksIndexed = 0;
        var processed = 0;
        var failed = new List<IngestionFailure>();

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();

            if (deadline.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Ingestion of {Path} hit the {TimeoutSeconds}s budget after {Processed}/{Total} documents",
                    command.Path, _options.TimeoutSeconds, processed, documents.Count);

                break;
            }

            var outcome = await IngestOneAsync(document, ct);
            processed++;

            switch (outcome.Kind)
            {
                case DocumentOutcomeKind.Ingested:
                    ingested++;
                    chunksIndexed += outcome.ChunkCount;
                    break;

                case DocumentOutcomeKind.Skipped:
                    skipped++;
                    break;

                default:
                    failed.Add(new IngestionFailure(document.Path, outcome.Reason!));
                    break;
            }
        }

        stopwatch.Stop();

        var remaining = documents.Count - processed;
        var status = remaining > 0 ? IngestionStatus.PartiallyCompleted : IngestionStatus.Completed;

        _logger.LogInformation(
            "Ingestion of {Path} finished {Status}: {Ingested}/{Total} ingested, {Chunks} chunks, " +
            "{Skipped} skipped, {Failed} failed, {Remaining} remaining",
            command.Path, status, ingested, documents.Count, chunksIndexed, skipped, failed.Count, remaining);

        return Result<IngestionSummary>.Success(new IngestionSummary(
            status,
            documents.Count,
            ingested,
            skipped,
            failed.Count,
            remaining,
            chunksIndexed,
            stopwatch.ElapsedMilliseconds,
            failed));
    }

    /// <summary>Runs one document through the pipeline, converting any expected failure into an outcome.</summary>
    private async Task<DocumentOutcome> IngestOneAsync(LocatedDocument document, CancellationToken ct)
    {
        var extracted = await document.Source.ExtractAsync(document.Path, ct);
        if (!extracted.IsSuccess)
        {
            return DocumentOutcome.Failed(extracted.Error!);
        }

        var raw = extracted.Value!;

        if (await _vectorStore.ExistsWithHashAsync(raw.SourceId, raw.ContentHash, ct))
        {
            _logger.LogDebug("Skipping unchanged document {SourceId}", raw.SourceId);
            return DocumentOutcome.Skipped();
        }

        // New or changed: drop any previous version so a shrunk document leaves no orphan chunks.
        await _vectorStore.DeleteBySourceIdAsync(raw.SourceId, ct);

        var chunks = _chunker.Chunk(raw);
        if (chunks.Count == 0)
        {
            return DocumentOutcome.Failed($"EmptyExtraction: {document.Path} produced no chunks.");
        }

        var vectors = await _embeddings.GenerateAsync([.. chunks.Select(c => c.Text)], ct);
        if (!vectors.IsSuccess)
        {
            return DocumentOutcome.Failed(vectors.Error!);
        }

        await _vectorStore.UpsertAsync(
            [.. chunks.Zip(vectors.Value!, (chunk, vector) => (chunk, vector))], ct);

        return DocumentOutcome.Ingested(chunks.Count);
    }

    private enum DocumentOutcomeKind
    {
        Ingested,
        Skipped,
        Failed
    }

    private readonly record struct DocumentOutcome(DocumentOutcomeKind Kind, int ChunkCount, string? Reason)
    {
        public static DocumentOutcome Ingested(int chunkCount) => new(DocumentOutcomeKind.Ingested, chunkCount, null);

        public static DocumentOutcome Skipped() => new(DocumentOutcomeKind.Skipped, 0, null);

        public static DocumentOutcome Failed(string reason) => new(DocumentOutcomeKind.Failed, 0, reason);
    }
}
