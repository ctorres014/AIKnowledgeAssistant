using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.Domain.Rag;

/// <summary>
/// A chunk returned by the vector search, with the similarity score that earned it a place
/// in the grounding context.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="DocumentChunk"/>: the Qdrant payload keeps neither
/// <c>TokenCount</c> nor the point id, and search contributes <see cref="Score"/>, which
/// ingestion never knows. Reusing <see cref="DocumentChunk"/> would mean inventing values.
/// </remarks>
/// <param name="SourceId">Stable identity of the parent source, carried for citation.</param>
/// <param name="SourceType">Origin format of the parent document.</param>
/// <param name="Title">Title of the parent document, carried for citation.</param>
/// <param name="ChunkIndex">0-based position of this chunk within the document.</param>
/// <param name="Text">Chunk text injected as grounding context.</param>
/// <param name="Score">Similarity score reported by the vector store.</param>
public sealed record RetrievedChunk(
    string SourceId,
    SourceType SourceType,
    string Title,
    int ChunkIndex,
    string Text,
    float Score);
