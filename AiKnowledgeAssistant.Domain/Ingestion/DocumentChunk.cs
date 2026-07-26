namespace AiKnowledgeAssistant.Domain.Ingestion;

/// <summary>
/// The indexable unit: a window of a <see cref="RawDocument"/> that becomes one Qdrant point.
/// </summary>
/// <param name="Id">Deterministic GUID derived from <c>$"{SourceId}#{Index}"</c>, so re-ingesting
/// the same document reuses point IDs instead of duplicating them.</param>
/// <param name="SourceId">Stable identity of the parent source.</param>
/// <param name="SourceType">Origin format of the parent document.</param>
/// <param name="Title">Title of the parent document, carried for citation.</param>
/// <param name="Index">0-based position of this chunk within the document.</param>
/// <param name="Text">Chunk text injected as grounding context.</param>
/// <param name="TokenCount">Estimated token count (~4 chars per token), not a real tokenizer.</param>
/// <param name="ContentHash">Hash of the parent document, not of this chunk.</param>
public sealed record DocumentChunk(
    Guid Id,
    string SourceId,
    SourceType SourceType,
    string Title,
    int Index,
    string Text,
    int TokenCount,
    string ContentHash);
