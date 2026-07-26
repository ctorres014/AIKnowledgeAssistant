using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Qdrant.Client.Grpc;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

public class QdrantPayloadTests
{
    private const long IngestedAt = 1_774_000_000_000;

    private static DocumentChunk Chunk(int index = 2) => new(
        ChunkIdFactory.ForChunk("c:/docs/handbook.md", index),
        "c:/docs/handbook.md",
        SourceType.Markdown,
        "handbook",
        index,
        "Working hours are flexible.",
        7,
        "abc123");

    [Fact]
    public void Build_ProducesExactlyTheSevenPayloadFields()
    {
        var payload = QdrantPayload.Build(Chunk(), IngestedAt);

        Assert.Equal(7, payload.Count);
        Assert.Equal(
            ["chunkIndex", "contentHash", "ingestedAt", "sourceId", "sourceType", "text", "title"],
            payload.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Build_MapsEveryFieldToTheChunkValue()
    {
        var chunk = Chunk(index: 4);

        var payload = QdrantPayload.Build(chunk, IngestedAt);

        Assert.Equal("c:/docs/handbook.md", payload["sourceId"].StringValue);
        Assert.Equal("Markdown", payload["sourceType"].StringValue);
        Assert.Equal("handbook", payload["title"].StringValue);
        Assert.Equal(4, payload["chunkIndex"].IntegerValue);
        Assert.Equal("Working hours are flexible.", payload["text"].StringValue);
        Assert.Equal("abc123", payload["contentHash"].StringValue);
        Assert.Equal(IngestedAt, payload["ingestedAt"].IntegerValue);
    }

    [Fact]
    public void IndexedFields_AreSourceIdAndContentHashOnly()
    {
        Assert.Equal(["sourceId", "contentHash"], QdrantPayload.IndexedFields);
    }

    [Fact]
    public void ToPoint_UsesTheDeterministicChunkGuidAsPointId()
    {
        var chunk = Chunk();

        var point = QdrantPayload.ToPoint(chunk, new float[768], IngestedAt);

        Assert.Equal(chunk.Id.ToString(), point.Id.Uuid);
    }

    [Fact]
    public void ToPoint_CarriesTheVectorAndThePayload()
    {
        var vector = Enumerable.Range(0, 768).Select(i => i * 0.5f).ToArray();

        var point = QdrantPayload.ToPoint(Chunk(), vector, IngestedAt);

        Assert.Equal(768, point.Vectors.Vector.Dense.Data.Count);
        Assert.Equal(vector, point.Vectors.Vector.Dense.Data);
        Assert.Equal(7, point.Payload.Count);
        Assert.Equal("handbook", point.Payload["title"].StringValue);
    }

    [Fact]
    public void ToPoint_IsStableForTheSameChunk()
    {
        var chunk = Chunk();

        var first = QdrantPayload.ToPoint(chunk, new float[768], IngestedAt);
        var second = QdrantPayload.ToPoint(chunk, new float[768], IngestedAt);

        Assert.Equal(first.Id.Uuid, second.Id.Uuid);
    }

    [Fact]
    public void BySourceId_MatchesTheSourceIdKeyword()
    {
        var filter = QdrantPayload.BySourceId("c:/docs/handbook.md");

        var condition = Assert.Single(filter.Must);
        Assert.Equal("sourceId", condition.Field.Key);
        Assert.Equal("c:/docs/handbook.md", condition.Field.Match.Keyword);
    }

    [Fact]
    public void BySourceIdAndHash_MatchesBothKeywords()
    {
        var filter = QdrantPayload.BySourceIdAndHash("c:/docs/handbook.md", "abc123");

        Assert.Equal(2, filter.Must.Count);

        var conditions = filter.Must.ToDictionary(c => c.Field.Key, c => c.Field.Match.Keyword);
        Assert.Equal("c:/docs/handbook.md", conditions["sourceId"]);
        Assert.Equal("abc123", conditions["contentHash"]);
    }

    [Theory]
    [InlineData("Cosine", Distance.Cosine)]
    [InlineData("cosine", Distance.Cosine)]
    [InlineData(" Dot ", Distance.Dot)]
    [InlineData("Euclid", Distance.Euclid)]
    [InlineData("euclidean", Distance.Euclid)]
    [InlineData("Manhattan", Distance.Manhattan)]
    public void ParseDistance_AcceptsTheSupportedMetrics(string configured, Distance expected)
    {
        Assert.Equal(expected, QdrantPayload.ParseDistance(configured));
    }

    [Theory]
    [InlineData("hamming")]
    [InlineData("")]
    public void ParseDistance_RejectsUnknownMetrics(string configured)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QdrantPayload.ParseDistance(configured));
    }
}
