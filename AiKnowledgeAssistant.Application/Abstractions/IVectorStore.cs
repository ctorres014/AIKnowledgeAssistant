using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;

namespace AiKnowledgeAssistant.Application.Abstractions;

/// <summary>
/// The vector index. In SPEC 02 its payload is also the only tracking of what has been
/// ingested — there is no relational store until SPEC 05.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Creates the collection with the configured dimension and distance if it is missing.
    /// Fails loudly when an existing collection has a different dimension, rather than
    /// mixing incompatible vector spaces.
    /// </summary>
    Task EnsureCollectionAsync(CancellationToken ct);

    /// <summary>True when the document is already indexed with this exact content hash (skip it).</summary>
    Task<bool> ExistsWithHashAsync(string sourceId, string contentHash, CancellationToken ct);

    /// <summary>Removes every chunk of a document, used before reindexing changed content.</summary>
    Task DeleteBySourceIdAsync(string sourceId, CancellationToken ct);

    /// <summary>Inserts or overwrites points. Chunk IDs are deterministic, so re-ingesting never duplicates.</summary>
    Task UpsertAsync(IReadOnlyList<(DocumentChunk Chunk, float[] Vector)> points, CancellationToken ct);

    /// <summary>Collection name, vector count and dimension, as surfaced by <c>GET /api/ingest/stats</c>.</summary>
    Task<VectorStoreStats> GetStatsAsync(CancellationToken ct);

    /// <summary>
    /// Nearest neighbours of a query vector: at most <paramref name="topK"/> chunks, none scoring
    /// below <paramref name="minScore"/>.
    /// </summary>
    /// <remarks>
    /// Alone among the members here this returns <c>Result</c> instead of letting exceptions surface.
    /// A store outage during ingestion is a background failure, but during a query it is an expected
    /// one that has to reach the caller as <c>503 VectorSearchFailed</c> rather than an opaque 500.
    /// </remarks>
    Task<Result<IReadOnlyList<RetrievedChunk>>> SearchAsync(
        float[] queryVector, int topK, float minScore, CancellationToken ct);
}

/// <summary>Snapshot of the vector collection.</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="VectorsCount">Number of indexed vectors.</param>
/// <param name="Dimension">Vector dimension of the collection.</param>
public sealed record VectorStoreStats(string Collection, long VectorsCount, int Dimension);
