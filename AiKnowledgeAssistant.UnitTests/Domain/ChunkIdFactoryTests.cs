using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.UnitTests.Domain;

public class ChunkIdFactoryTests
{
    [Fact]
    public void ForChunk_SameSourceAndIndex_ReturnsSameGuid()
    {
        var first = ChunkIdFactory.ForChunk("c:/docs/handbook.md", 3);
        var second = ChunkIdFactory.ForChunk("c:/docs/handbook.md", 3);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ForChunk_DifferentIndex_ReturnsDifferentGuid()
    {
        var first = ChunkIdFactory.ForChunk("c:/docs/handbook.md", 0);
        var second = ChunkIdFactory.ForChunk("c:/docs/handbook.md", 1);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForChunk_DifferentSource_ReturnsDifferentGuid()
    {
        var first = ChunkIdFactory.ForChunk("c:/docs/handbook.md", 0);
        var second = ChunkIdFactory.ForChunk("c:/docs/other.md", 0);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForChunk_NeverReturnsEmptyGuid()
    {
        Assert.NotEqual(Guid.Empty, ChunkIdFactory.ForChunk("c:/docs/handbook.md", 0));
    }

    /// <summary>
    /// Golden values computed outside the implementation. These pin the IDs across process
    /// restarts and rebuilds — the property that lets a re-ingest overwrite points instead of
    /// duplicating them.
    /// </summary>
    [Theory]
    [InlineData("c:/docs/handbook.md", 0, "65c847f8-cb4d-5f0c-858c-34a3df925d5f")]
    [InlineData("c:/docs/handbook.md", 1, "51e5fd56-e2bb-5494-8de6-afa2fffe3dca")]
    [InlineData("c:/docs/other.md", 0, "4f7dcb23-05ce-5e0a-897c-da618b8515e6")]
    public void ForChunk_MatchesPinnedIdentity(string sourceId, int index, string expected)
    {
        Assert.Equal(Guid.Parse(expected), ChunkIdFactory.ForChunk(sourceId, index));
    }

    [Fact]
    public void ForChunk_ProducesRfc4122Version5Layout()
    {
        var id = ChunkIdFactory.ForChunk("c:/docs/handbook.md", 7);
        var bytes = id.ToByteArray(bigEndian: true);

        Assert.Equal(0x50, bytes[6] & 0xF0);  // version 5
        Assert.Equal(0x80, bytes[8] & 0xC0);  // RFC 4122 variant
    }

    [Fact]
    public void ForChunk_IndexIsNotConfusableWithSourceIdSuffix()
    {
        // "a#1" + index 0 must not collide with "a" + index 1 style inputs.
        Assert.NotEqual(
            ChunkIdFactory.ForChunk("a#1", 0),
            ChunkIdFactory.ForChunk("a", 1));
    }
}
