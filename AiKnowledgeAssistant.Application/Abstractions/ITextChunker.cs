using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.Application.Abstractions;

/// <summary>
/// Splits an extracted document into indexable windows. Pure and synchronous: no I/O.
/// </summary>
public interface ITextChunker
{
    /// <summary>
    /// Produces chunks with <c>Index</c> consecutive from 0. A document short enough to fit
    /// the window yields exactly one chunk.
    /// </summary>
    IReadOnlyList<DocumentChunk> Chunk(RawDocument document);
}
