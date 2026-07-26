using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.UnitTests.Application;

/// <summary>Covers the two bounds on a synchronous ingest: the file cap and the time budget.</summary>
public class IngestDocumentsHandlerLimitsTests
{
    private readonly InMemoryVectorStore _store = new();

    private static FixedWindowTextChunker Chunker() =>
        new(Options.Create(new ChunkingOptions { MaxTokens = 500, OverlapTokens = 50 }));

    /// <summary>A corpus of <paramref name="count"/> readable markdown documents.</summary>
    private static (FakeDocumentSource Source, string[] Paths) Corpus(int count)
    {
        var source = new FakeDocumentSource();
        var paths = new string[count];

        for (var i = 0; i < count; i++)
        {
            paths[i] = $"c:/docs/doc-{i:D3}.md";
            source.WithDocument(paths[i], $"# Document {i}\n\nBody text for document number {i}.");
        }

        return (source, paths);
    }

    private IngestDocumentsHandler CreateHandler(
        FakeDocumentSource source,
        string[] paths,
        IngestionOptions options,
        FakeEmbeddingGenerator? embeddings = null) =>
        new(
            FakeDocumentLocator.Returning(source, paths),
            Chunker(),
            embeddings ?? new FakeEmbeddingGenerator(),
            _store,
            Options.Create(options),
            NullLogger<IngestDocumentsHandler>.Instance);

    [Fact]
    public async Task HandleAsync_MoreFilesThanTheCap_FailsWithTooManyFilesAndIndexesNothing()
    {
        var (source, paths) = Corpus(101);
        var handler = CreateHandler(source, paths, new IngestionOptions { MaxFilesPerRequest = 100 });

        var result = await handler.HandleAsync(new IngestDocumentsCommand("c:/docs", null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TooManyFiles", result.ErrorCode);
        Assert.Contains("101", result.Error, StringComparison.Ordinal);
        Assert.Contains("100", result.Error, StringComparison.Ordinal);

        Assert.Equal(0, _store.VectorsCount);
        Assert.Equal(0, _store.UpsertCalls);
        Assert.Equal(0, _store.DeleteCalls);
    }

    [Fact]
    public async Task HandleAsync_ExactlyTheCap_IsAccepted()
    {
        var (source, paths) = Corpus(100);
        var handler = CreateHandler(source, paths, new IngestionOptions { MaxFilesPerRequest = 100 });

        var result = await handler.HandleAsync(new IngestDocumentsCommand("c:/docs", null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(100, result.Value!.Ingested);
        Assert.Equal(IngestionStatus.Completed, result.Value!.Status);
    }

    [Fact]
    public async Task HandleAsync_TimeBudgetExpires_ReturnsPartiallyCompletedWithRemaining()
    {
        var (source, paths) = Corpus(6);

        // ~600ms per document against a 1s budget: the first documents land, the rest do not.
        var slow = new FakeEmbeddingGenerator(delayPerCall: TimeSpan.FromMilliseconds(600));
        var handler = CreateHandler(source, paths, new IngestionOptions { TimeoutSeconds = 1 }, slow);

        var result = await handler.HandleAsync(new IngestDocumentsCommand("c:/docs", null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var summary = result.Value!;

        Assert.Equal(IngestionStatus.PartiallyCompleted, summary.Status);
        Assert.True(summary.Ingested > 0, "expected at least one document to be ingested");
        Assert.True(summary.Remaining > 0, "expected unprocessed documents to be reported");
        Assert.Equal(6, summary.TotalFiles);
        Assert.Equal(summary.TotalFiles, summary.Ingested + summary.Skipped + summary.FailedCount + summary.Remaining);
    }

    [Fact]
    public async Task HandleAsync_TimeBudgetExpires_NeverLeavesADocumentHalfIndexed()
    {
        var (source, paths) = Corpus(6);
        var slow = new FakeEmbeddingGenerator(delayPerCall: TimeSpan.FromMilliseconds(600));
        var handler = CreateHandler(source, paths, new IngestionOptions { TimeoutSeconds = 1 }, slow);

        var summary = (await handler.HandleAsync(
            new IngestDocumentsCommand("c:/docs", null), CancellationToken.None)).Value!;

        // Every document present in the store has all of its chunks, numbered from 0.
        foreach (var sourceId in _store.SourceIds)
        {
            var chunks = _store.ChunksOf(sourceId);
            Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
        }

        Assert.Equal(summary.Ingested, _store.SourceIds.Count);
    }

    [Fact]
    public async Task HandleAsync_RetryAfterPartial_ResumesAndCompletes()
    {
        var (source, paths) = Corpus(6);
        var slow = new FakeEmbeddingGenerator(delayPerCall: TimeSpan.FromMilliseconds(600));

        var partial = (await CreateHandler(source, paths, new IngestionOptions { TimeoutSeconds = 1 }, slow)
            .HandleAsync(new IngestDocumentsCommand("c:/docs", null), CancellationToken.None)).Value!;

        Assert.Equal(IngestionStatus.PartiallyCompleted, partial.Status);

        // Same request, generous budget: the already stored documents come back as skipped.
        var second = (await CreateHandler(source, paths, new IngestionOptions { TimeoutSeconds = 90 })
            .HandleAsync(new IngestDocumentsCommand("c:/docs", null), CancellationToken.None)).Value!;

        Assert.Equal(IngestionStatus.Completed, second.Status);
        Assert.Equal(0, second.Remaining);
        Assert.Equal(partial.Ingested, second.Skipped);
        Assert.Equal(6, second.Ingested + second.Skipped);
        Assert.Equal(6, _store.SourceIds.Count);
    }

    [Fact]
    public async Task HandleAsync_GenerousBudget_CompletesWithoutRemaining()
    {
        var (source, paths) = Corpus(5);
        var handler = CreateHandler(source, paths, new IngestionOptions { TimeoutSeconds = 90 });

        var summary = (await handler.HandleAsync(
            new IngestDocumentsCommand("c:/docs", null), CancellationToken.None)).Value!;

        Assert.Equal(IngestionStatus.Completed, summary.Status);
        Assert.Equal(0, summary.Remaining);
        Assert.Equal(5, summary.Ingested);
    }

    [Fact]
    public async Task HandleAsync_CallerCancels_PropagatesInsteadOfReturningPartial()
    {
        var (source, paths) = Corpus(3);
        var handler = CreateHandler(source, paths, new IngestionOptions());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(new IngestDocumentsCommand("c:/docs", null), cts.Token));
    }
}
