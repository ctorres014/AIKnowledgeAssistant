using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

/// <summary>
/// Search path of the store against a mocked Qdrant client: what it asks Qdrant for, how it maps the
/// answer back, and that an outage becomes a <c>Result</c> instead of an exception.
/// </summary>
public class QdrantVectorStoreSearchTests
{
    private const string Collection = "knowledge";

    private readonly Mock<IQdrantClient> _client = new(MockBehavior.Strict);

    private QdrantVectorStore CreateStore() => new(
        _client.Object,
        Options.Create(new VectorStoreOptions { CollectionName = Collection, Distance = "Cosine" }),
        Options.Create(new EmbeddingOptions { Dimension = 768 }),
        NullLogger<QdrantVectorStore>.Instance);

    /// <summary>Every optional argument of the client's overload, so one setup covers any call shape.</summary>
    private void SetupSearch(Func<IReadOnlyList<ScoredPoint>> result) =>
        _client
            .Setup(c => c.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<ReadOnlyMemory<float>>(),
                It.IsAny<Filter>(),
                It.IsAny<SearchParams>(),
                It.IsAny<ulong>(),
                It.IsAny<ulong>(),
                It.IsAny<WithPayloadSelector>(),
                It.IsAny<WithVectorsSelector>(),
                It.IsAny<float?>(),
                It.IsAny<string>(),
                It.IsAny<ReadConsistency>(),
                It.IsAny<ShardKeySelector>(),
                It.IsAny<ReadOnlyMemory<uint>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(result()));

    private static ScoredPoint Point(
        float score,
        string sourceId = "c:/docs/handbook.md",
        string sourceType = "Markdown",
        string title = "handbook",
        int chunkIndex = 3,
        string text = "Onboarding runs for five working days.")
    {
        var point = new ScoredPoint { Id = Guid.NewGuid(), Score = score };

        point.Payload[QdrantPayload.SourceIdField] = sourceId;
        point.Payload[QdrantPayload.SourceTypeField] = sourceType;
        point.Payload[QdrantPayload.TitleField] = title;
        point.Payload[QdrantPayload.ChunkIndexField] = chunkIndex;
        point.Payload[QdrantPayload.TextField] = text;
        point.Payload[QdrantPayload.ContentHashField] = "abc123";
        point.Payload[QdrantPayload.IngestedAtField] = 1_774_000_000_000;

        return point;
    }

    [Fact]
    public async Task SearchAsync_MapsThePayloadFieldsAndTheScore()
    {
        SetupSearch(() => [Point(0.82f)]);

        var result = await CreateStore().SearchAsync(new float[768], topK: 5, minScore: 0.5f, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var chunk = Assert.Single(result.Value!);
        Assert.Equal("c:/docs/handbook.md", chunk.SourceId);
        Assert.Equal(SourceType.Markdown, chunk.SourceType);
        Assert.Equal("handbook", chunk.Title);
        Assert.Equal(3, chunk.ChunkIndex);
        Assert.Equal("Onboarding runs for five working days.", chunk.Text);
        Assert.Equal(0.82f, chunk.Score);
    }

    [Fact]
    public async Task SearchAsync_PassesCollectionTopKAndMinScoreToQdrant()
    {
        var vector = Enumerable.Range(0, 768).Select(i => i * 0.001f).ToArray();
        SetupSearch(() => []);

        await CreateStore().SearchAsync(vector, topK: 5, minScore: 0.5f, CancellationToken.None);

        _client.Verify(c => c.SearchAsync(
            Collection,
            It.Is<ReadOnlyMemory<float>>(v => v.ToArray().SequenceEqual(vector)),
            It.IsAny<Filter>(),
            It.IsAny<SearchParams>(),
            5ul,
            It.IsAny<ulong>(),
            It.IsAny<WithPayloadSelector>(),
            It.IsAny<WithVectorsSelector>(),
            0.5f,
            It.IsAny<string>(),
            It.IsAny<ReadConsistency>(),
            It.IsAny<ShardKeySelector>(),
            It.IsAny<ReadOnlyMemory<uint>?>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_RequestsThePayloadSoCitationsCanBeBuilt()
    {
        SetupSearch(() => []);

        await CreateStore().SearchAsync(new float[768], topK: 5, minScore: 0.5f, CancellationToken.None);

        _client.Verify(c => c.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<ReadOnlyMemory<float>>(),
            It.IsAny<Filter>(),
            It.IsAny<SearchParams>(),
            It.IsAny<ulong>(),
            It.IsAny<ulong>(),
            It.Is<WithPayloadSelector>(s => s.Enable),
            It.IsAny<WithVectorsSelector>(),
            It.IsAny<float?>(),
            It.IsAny<string>(),
            It.IsAny<ReadConsistency>(),
            It.IsAny<ShardKeySelector>(),
            It.IsAny<ReadOnlyMemory<uint>?>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_PreservesTheOrderQdrantReturned()
    {
        SetupSearch(() =>
        [
            Point(0.91f, title: "first"),
            Point(0.77f, title: "second"),
            Point(0.61f, title: "third")
        ]);

        var result = await CreateStore().SearchAsync(new float[768], topK: 5, minScore: 0.5f, CancellationToken.None);

        Assert.Equal(["first", "second", "third"], result.Value!.Select(c => c.Title));
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWhenNothingClearedTheThreshold()
    {
        SetupSearch(() => []);

        var result = await CreateStore().SearchAsync(new float[768], topK: 5, minScore: 0.9f, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task SearchAsync_TurnsAClientFailureIntoVectorSearchFailed()
    {
        SetupSearch(() => throw new HttpRequestException("connection refused"));

        var result = await CreateStore().SearchAsync(new float[768], topK: 5, minScore: 0.5f, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VectorSearchFailed", result.ErrorCode);
    }

    [Fact]
    public async Task SearchAsync_DoesNotSwallowCallerCancellation()
    {
        SetupSearch(() => throw new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateStore().SearchAsync(new float[768], topK: 5, minScore: 0.5f, cts.Token));
    }

    [Fact]
    public async Task SearchAsync_DegradesAnUnknownSourceTypeInsteadOfFailingTheQuery()
    {
        SetupSearch(() => [Point(0.8f, sourceType: "Notion")]);

        var result = await CreateStore().SearchAsync(new float[768], topK: 5, minScore: 0.5f, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SourceType.Text, Assert.Single(result.Value!).SourceType);
    }
}
