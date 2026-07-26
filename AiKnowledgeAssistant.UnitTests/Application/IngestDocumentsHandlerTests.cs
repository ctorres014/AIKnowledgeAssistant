using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.UnitTests.Application;

public class IngestDocumentsHandlerTests
{
    private static readonly string[] ThreePaths = ["c:/docs/a.md", "c:/docs/b.md", "c:/docs/c.md"];

    private readonly InMemoryVectorStore _store = new();
    private readonly FakeEmbeddingGenerator _embeddings = new();

    private static FixedWindowTextChunker Chunker() =>
        new(Options.Create(new ChunkingOptions { MaxTokens = 500, OverlapTokens = 50 }));

    private IngestDocumentsHandler CreateHandler(
        FakeDocumentSource source,
        string[]? paths = null,
        FakeEmbeddingGenerator? embeddings = null,
        IngestionOptions? options = null) =>
        new(
            FakeDocumentLocator.Returning(source, paths ?? ThreePaths),
            Chunker(),
            embeddings ?? _embeddings,
            _store,
            Options.Create(options ?? new IngestionOptions()),
            NullLogger<IngestDocumentsHandler>.Instance);

    private static FakeDocumentSource ThreeGoodDocuments() => new FakeDocumentSource()
        .WithDocument("c:/docs/a.md", "# A\n\nThe first policy document.")
        .WithDocument("c:/docs/b.md", "# B\n\nThe second policy document.")
        .WithDocument("c:/docs/c.md", "# C\n\nThe third policy document.");

    private Task<global::AiKnowledgeAssistant.Domain.Common.Result<IngestionSummary>> Ingest(
        IngestDocumentsHandler handler) =>
        handler.HandleAsync(new IngestDocumentsCommand("c:/docs", null), CancellationToken.None);

    [Fact]
    public async Task HandleAsync_AllDocumentsReadable_IngestsEveryOne()
    {
        var handler = CreateHandler(ThreeGoodDocuments());

        var result = await Ingest(handler);

        Assert.True(result.IsSuccess, result.Error);
        var summary = result.Value!;

        Assert.Equal(IngestionStatus.Completed, summary.Status);
        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(3, summary.Ingested);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal(0, summary.Remaining);
        Assert.True(summary.ChunksIndexed >= 3);
        Assert.Empty(summary.Failed);
    }

    [Fact]
    public async Task HandleAsync_OneOfThreeFails_IngestsTheOtherTwoAndReportsTheReason()
    {
        var source = new FakeDocumentSource()
            .WithDocument("c:/docs/a.md", "# A\n\nThe first policy document.")
            .WithFailure("c:/docs/b.md", "PdfExtractionFailed: unexpected end of stream")
            .WithDocument("c:/docs/c.md", "# C\n\nThe third policy document.");

        var result = await Ingest(CreateHandler(source));

        var summary = result.Value!;

        Assert.Equal(2, summary.Ingested);
        Assert.Equal(1, summary.FailedCount);

        var failure = Assert.Single(summary.Failed);
        Assert.Equal("c:/docs/b.md", failure.Path);
        Assert.Contains("PdfExtractionFailed", failure.Reason, StringComparison.Ordinal);

        // The failure did not stop the run, and nothing was indexed for it.
        Assert.Equal(2, _store.SourceIds.Count);
        Assert.Empty(_store.ChunksOf("c:/docs/b.md"));
    }

    [Fact]
    public async Task HandleAsync_SecondRunWithoutChanges_SkipsEverythingAndIndexesNothing()
    {
        var source = ThreeGoodDocuments();

        var first = await Ingest(CreateHandler(source));
        var vectorsAfterFirst = _store.VectorsCount;

        var second = await Ingest(CreateHandler(source));
        var summary = second.Value!;

        Assert.Equal(3, first.Value!.Ingested);
        Assert.Equal(3, summary.Skipped);
        Assert.Equal(0, summary.Ingested);
        Assert.Equal(0, summary.ChunksIndexed);
        Assert.Equal(IngestionStatus.Completed, summary.Status);
        Assert.Equal(vectorsAfterFirst, _store.VectorsCount);
    }

    [Fact]
    public async Task HandleAsync_SecondRunWithoutChanges_DoesNotRegenerateEmbeddings()
    {
        var source = ThreeGoodDocuments();

        await Ingest(CreateHandler(source));
        var callsAfterFirst = _embeddings.Calls;

        await Ingest(CreateHandler(source));

        Assert.Equal(callsAfterFirst, _embeddings.Calls);
    }

    [Fact]
    public async Task HandleAsync_ChangedDocument_ReplacesItsChunksInsteadOfAccumulating()
    {
        var longContent = string.Join("\n\n", Enumerable.Range(0, 40)
            .Select(i => $"Paragraph {i} of the original document, long enough to force several chunks."));

        var source = new FakeDocumentSource().WithDocument("c:/docs/a.md", longContent);
        await Ingest(CreateHandler(source, ["c:/docs/a.md"]));

        var chunksBefore = _store.ChunksOf("c:/docs/a.md").Count;
        Assert.True(chunksBefore > 1);

        // Same path, much shorter content: the old chunks must go.
        var changed = new FakeDocumentSource().WithDocument("c:/docs/a.md", "# A\n\nRewritten and much shorter.");
        var result = await Ingest(CreateHandler(changed, ["c:/docs/a.md"]));

        var summary = result.Value!;
        Assert.Equal(1, summary.Ingested);
        Assert.Equal(0, summary.Skipped);

        var chunksAfter = _store.ChunksOf("c:/docs/a.md");
        Assert.Single(chunksAfter);
        Assert.Equal(1, _store.VectorsCount);
        Assert.Contains("Rewritten", chunksAfter[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_EmbeddingProviderDown_FailsEveryDocumentWithoutThrowing()
    {
        var embeddings = new FakeEmbeddingGenerator { FailAlways = true };

        var result = await Ingest(CreateHandler(ThreeGoodDocuments(), embeddings: embeddings));

        var summary = result.Value!;

        Assert.True(result.IsSuccess);
        Assert.Equal(0, summary.Ingested);
        Assert.Equal(3, summary.FailedCount);
        Assert.Equal(0, summary.ChunksIndexed);
        Assert.All(summary.Failed, f => Assert.Contains("EmbeddingRequestFailed", f.Reason, StringComparison.Ordinal));
        Assert.Equal(0, _store.VectorsCount);
    }

    [Fact]
    public async Task HandleAsync_IndexesOneVectorPerChunkWithTheDocumentMetadata()
    {
        await Ingest(CreateHandler(ThreeGoodDocuments()));

        Assert.Equal(3, _store.VectorsCount);
        Assert.All(_store.Points, p =>
        {
            Assert.Equal(768, p.Vector.Length);
            Assert.Equal(SourceType.Markdown, p.Chunk.SourceType);
            Assert.NotEqual(Guid.Empty, p.Chunk.Id);
        });
    }

    [Fact]
    public async Task HandleAsync_ChunkIndexesAreConsecutivePerDocument()
    {
        var longContent = string.Join("\n\n", Enumerable.Range(0, 40)
            .Select(i => $"Paragraph {i} carries enough text to spill across more than one window."));

        var source = new FakeDocumentSource().WithDocument("c:/docs/a.md", longContent);
        await Ingest(CreateHandler(source, ["c:/docs/a.md"]));

        var chunks = _store.ChunksOf("c:/docs/a.md");

        Assert.True(chunks.Count > 1);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public async Task HandleAsync_MissingFile_ReportsItAsFailedNotAsSkipped()
    {
        // Only two of the three located paths are readable.
        var source = new FakeDocumentSource()
            .WithDocument("c:/docs/a.md", "# A\n\nContent.")
            .WithDocument("c:/docs/c.md", "# C\n\nContent.");

        var summary = (await Ingest(CreateHandler(source))).Value!;

        Assert.Equal(2, summary.Ingested);
        Assert.Equal(1, summary.FailedCount);
        Assert.Contains("FileNotFound", Assert.Single(summary.Failed).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_LocatorFailure_PropagatesTheErrorCode()
    {
        var handler = new IngestDocumentsHandler(
            FakeDocumentLocator.Failing("PathNotFound: c:/nope", "PathNotFound"),
            Chunker(),
            _embeddings,
            _store,
            Options.Create(new IngestionOptions()),
            NullLogger<IngestDocumentsHandler>.Instance);

        var result = await handler.HandleAsync(new IngestDocumentsCommand("c:/nope", null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PathNotFound", result.ErrorCode);
        Assert.Equal(0, _store.VectorsCount);
    }

    [Fact]
    public async Task HandleAsync_ReportsANonNegativeDuration()
    {
        var summary = (await Ingest(CreateHandler(ThreeGoodDocuments()))).Value!;

        Assert.True(summary.DurationMs >= 0);
    }

    [Fact]
    public async Task HandleAsync_FailedCountMatchesTheFailedList()
    {
        var source = new FakeDocumentSource()
            .WithFailure("c:/docs/a.md")
            .WithFailure("c:/docs/b.md")
            .WithDocument("c:/docs/c.md", "# C\n\nContent.");

        var summary = (await Ingest(CreateHandler(source))).Value!;

        Assert.Equal(summary.Failed.Count, summary.FailedCount);
        Assert.Equal(2, summary.FailedCount);
    }
}
