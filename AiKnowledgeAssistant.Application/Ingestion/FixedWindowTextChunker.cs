using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Ingestion;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>
/// Fixed-window chunker: slices the document into windows of at most <see cref="ChunkingOptions.MaxTokens"/>
/// estimated tokens, repeating <see cref="ChunkingOptions.OverlapTokens"/> from the tail of the previous
/// window so context is not lost at the seam. When a window would cut mid-text it backs off to the last
/// paragraph break, then to the last sentence end, before falling back to a hard cut.
/// </summary>
/// <remarks>
/// Token counts are estimated at ~4 characters per token (spec decision — no real tokenizer). The
/// estimate can drift on code-heavy or CJK text; <c>MaxTokens: 500</c> leaves ample headroom under the
/// 8192-token context of <c>nomic-embed-text</c>, so drift does not break embedding.
/// </remarks>
public sealed class FixedWindowTextChunker : ITextChunker
{
    private const int CharsPerToken = 4;
    private const string ParagraphBreak = "\n\n";
    private const string SentenceBreak = ". ";

    private readonly int _windowChars;
    private readonly int _overlapChars;

    public FixedWindowTextChunker(IOptions<ChunkingOptions> options)
    {
        var value = options.Value;

        ArgumentOutOfRangeException.ThrowIfLessThan(value.MaxTokens, 1, nameof(ChunkingOptions.MaxTokens));
        ArgumentOutOfRangeException.ThrowIfNegative(value.OverlapTokens, nameof(ChunkingOptions.OverlapTokens));

        _windowChars = value.MaxTokens * CharsPerToken;

        // An overlap at or above the window size would never advance; clamp so progress is guaranteed.
        _overlapChars = Math.Min(value.OverlapTokens, value.MaxTokens - 1) * CharsPerToken;
    }

    public IReadOnlyList<DocumentChunk> Chunk(RawDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = Normalize(document.Content);
        if (text.Length == 0)
        {
            return [];
        }

        var chunks = new List<DocumentChunk>();
        var start = 0;

        while (start < text.Length)
        {
            var remaining = text.Length - start;
            var length = remaining <= _windowChars ? remaining : WindowLength(text, start);

            var slice = text.AsSpan(start, length).Trim().ToString();
            if (slice.Length > 0)
            {
                chunks.Add(new DocumentChunk(
                    ChunkIdFactory.ForChunk(document.SourceId, chunks.Count),
                    document.SourceId,
                    document.SourceType,
                    document.Title,
                    chunks.Count,
                    slice,
                    EstimateTokens(slice.Length),
                    document.ContentHash));
            }

            if (start + length >= text.Length)
            {
                break;
            }

            var next = start + length - _overlapChars;
            start = next > start ? next : start + length;
        }

        return chunks;
    }

    /// <summary>Estimated tokens for a character count, rounded up.</summary>
    private static int EstimateTokens(int charCount) => (charCount + CharsPerToken - 1) / CharsPerToken;

    private static string Normalize(string? content) =>
        (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>
    /// Length of the next window: the hard limit, pulled back to the last paragraph or sentence
    /// boundary in the second half of the window when one exists.
    /// </summary>
    private int WindowLength(string text, int start)
    {
        var end = start + _windowChars;
        var floor = start + (_windowChars / 2);

        var paragraph = LastIndexIn(text, ParagraphBreak, floor, end);
        if (paragraph >= 0)
        {
            return paragraph + ParagraphBreak.Length - start;
        }

        var sentence = LastIndexIn(text, SentenceBreak, floor, end);
        if (sentence >= 0)
        {
            return sentence + SentenceBreak.Length - start;
        }

        return _windowChars;
    }

    /// <summary>Last index of <paramref name="needle"/> fully contained in <c>[floor, endExclusive)</c>, or -1.</summary>
    private static int LastIndexIn(string text, string needle, int floor, int endExclusive)
    {
        var last = endExclusive - needle.Length;
        if (last < floor)
        {
            return -1;
        }

        return text.LastIndexOf(needle, last, last - floor + 1, StringComparison.Ordinal);
    }
}
