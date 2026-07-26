using System.Text;
using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Domain.Ingestion;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.UnitTests.Application;

public class FixedWindowTextChunkerTests
{
    private const int MaxTokens = 500;
    private const int OverlapTokens = 50;

    private static FixedWindowTextChunker CreateChunker(int maxTokens = MaxTokens, int overlapTokens = OverlapTokens) =>
        new(Options.Create(new ChunkingOptions { MaxTokens = maxTokens, OverlapTokens = overlapTokens }));

    private static RawDocument Document(string content) =>
        new("c:/docs/handbook.md", SourceType.Markdown, "handbook", content, "hash-abc");

    /// <summary>Builds prose of roughly <paramref name="tokens"/> estimated tokens (~4 chars each).</summary>
    private static string TextOfTokens(int tokens)
    {
        var builder = new StringBuilder();
        var sentence = 0;

        while (builder.Length < tokens * 4)
        {
            builder.Append($"Sentence number {sentence} explains one more corporate policy in detail. ");
            if (sentence % 5 == 4)
            {
                builder.Append("\n\n");
            }

            sentence++;
        }

        return builder.ToString();
    }

    [Fact]
    public void Chunk_ShortDocument_ProducesExactlyOneChunk()
    {
        var document = Document("A single short paragraph that fits well inside the window.");

        var chunks = CreateChunker().Chunk(document);

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.Index);
        Assert.Equal(document.Content, chunk.Text);
        Assert.True(chunk.TokenCount < MaxTokens);
    }

    [Fact]
    public void Chunk_DocumentJustUnderTheWindow_ProducesExactlyOneChunk()
    {
        var chunks = CreateChunker().Chunk(Document(TextOfTokens(400)));

        Assert.Single(chunks);
    }

    [Fact]
    public void Chunk_LargeDocument_ProducesSeveralChunksWithinTheTokenBudget()
    {
        var chunks = CreateChunker().Chunk(Document(TextOfTokens(2_000)));

        Assert.True(chunks.Count > 1, $"expected more than one chunk, got {chunks.Count}");
        Assert.All(chunks, c => Assert.True(c.TokenCount <= MaxTokens, $"chunk {c.Index} had {c.TokenCount} tokens"));
    }

    [Fact]
    public void Chunk_LargeDocument_NumbersChunksConsecutivelyFromZero()
    {
        var chunks = CreateChunker().Chunk(Document(TextOfTokens(2_000)));

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public void Chunk_LargeDocument_OverlapsContiguousChunks()
    {
        var chunks = CreateChunker().Chunk(Document(TextOfTokens(2_000)));

        Assert.True(chunks.Count > 1);

        for (var i = 0; i < chunks.Count - 1; i++)
        {
            // The overlap window is 50 tokens (~200 chars); assert a safe slice of the tail of one
            // chunk reappears at the head of the next, leaving room for boundary trimming.
            var tail = chunks[i].Text[^150..];

            Assert.Contains(tail, chunks[i + 1].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Chunk_CarriesDocumentMetadataOntoEveryChunk()
    {
        var document = Document(TextOfTokens(2_000));

        var chunks = CreateChunker().Chunk(document);

        Assert.All(chunks, c =>
        {
            Assert.Equal(document.SourceId, c.SourceId);
            Assert.Equal(document.SourceType, c.SourceType);
            Assert.Equal(document.Title, c.Title);
            Assert.Equal(document.ContentHash, c.ContentHash);
            Assert.NotEqual(Guid.Empty, c.Id);
        });
    }

    [Fact]
    public void Chunk_PrefersParagraphBoundaries()
    {
        // Two paragraphs, the break sitting in the second half of the window.
        var first = new string('a', 1_500) + ".";
        var content = $"{first}\n\n{new string('b', 1_500)}.";

        var chunks = CreateChunker().Chunk(Document(content));

        Assert.True(chunks.Count > 1);
        Assert.EndsWith(first, chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_EmptyOrWhitespaceContent_ProducesNoChunks()
    {
        Assert.Empty(CreateChunker().Chunk(Document(string.Empty)));
        Assert.Empty(CreateChunker().Chunk(Document("   \n\n  \t ")));
    }

    [Fact]
    public void Chunk_WithOverlapNotBelowWindow_StillTerminates()
    {
        // Degenerate configuration: the clamp must keep the loop advancing.
        var chunks = CreateChunker(maxTokens: 50, overlapTokens: 500).Chunk(Document(TextOfTokens(400)));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 50));
    }

    [Fact]
    public void Chunk_IsDeterministic_AcrossInvocations()
    {
        var document = Document(TextOfTokens(2_000));

        var first = CreateChunker().Chunk(document);
        var second = CreateChunker().Chunk(document);

        Assert.Equal(first.Select(c => c.Id), second.Select(c => c.Id));
        Assert.Equal(first.Select(c => c.Text), second.Select(c => c.Text));
    }

    [Fact]
    public void Chunk_NormalizesWindowsLineEndings()
    {
        var chunks = CreateChunker().Chunk(Document("first line\r\nsecond line"));

        Assert.Equal("first line\nsecond line", Assert.Single(chunks).Text);
    }
}
