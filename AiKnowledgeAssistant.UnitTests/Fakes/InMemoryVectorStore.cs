using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;

namespace AiKnowledgeAssistant.UnitTests.Fakes;

/// <summary>
/// In-memory stand-in for Qdrant, keyed by point ID like the real store, so idempotency and
/// delete-before-reindex behaviour can be asserted without a container.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<Guid, (DocumentChunk Chunk, float[] Vector)> _points = [];

    public InMemoryVectorStore(string collection = "knowledge", int dimension = 768)
    {
        Collection = collection;
        Dimension = dimension;
    }

    public string Collection { get; }

    public int Dimension { get; }

    public int EnsureCollectionCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    public int UpsertCalls { get; private set; }

    public int SearchCalls { get; private set; }

    /// <summary>Arguments of the last <see cref="SearchAsync"/> call, for asserting what the pipeline asked for.</summary>
    public (float[] Vector, int TopK, float MinScore)? LastSearch { get; private set; }

    /// <summary>Set to fail every search, simulating a store outage during a query.</summary>
    public bool FailSearch { get; init; }

    public IReadOnlyCollection<(DocumentChunk Chunk, float[] Vector)> Points => _points.Values;

    public long VectorsCount => _points.Count;

    /// <summary>Chunks currently indexed for one document.</summary>
    public IReadOnlyList<DocumentChunk> ChunksOf(string sourceId) =>
    [
        .. _points.Values
            .Select(p => p.Chunk)
            .Where(c => string.Equals(c.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Index)
    ];

    /// <summary>Distinct documents currently indexed.</summary>
    public IReadOnlyCollection<string> SourceIds =>
        [.. _points.Values.Select(p => p.Chunk.SourceId).Distinct(StringComparer.OrdinalIgnoreCase)];

    public Task EnsureCollectionAsync(CancellationToken ct)
    {
        EnsureCollectionCalls++;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsWithHashAsync(string sourceId, string contentHash, CancellationToken ct) =>
        Task.FromResult(_points.Values.Any(p =>
            string.Equals(p.Chunk.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            p.Chunk.ContentHash == contentHash));

    public Task DeleteBySourceIdAsync(string sourceId, CancellationToken ct)
    {
        DeleteCalls++;

        foreach (var id in _points
            .Where(p => string.Equals(p.Value.Chunk.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToArray())
        {
            _points.Remove(id);
        }

        return Task.CompletedTask;
    }

    public Task UpsertAsync(IReadOnlyList<(DocumentChunk Chunk, float[] Vector)> points, CancellationToken ct)
    {
        UpsertCalls++;

        foreach (var point in points)
        {
            _points[point.Chunk.Id] = point;
        }

        return Task.CompletedTask;
    }

    public Task<VectorStoreStats> GetStatsAsync(CancellationToken ct) =>
        Task.FromResult(new VectorStoreStats(Collection, _points.Count, Dimension));

    /// <summary>Real cosine similarity over the stored vectors, filtered and truncated like Qdrant does.</summary>
    public Task<Result<IReadOnlyList<RetrievedChunk>>> SearchAsync(
        float[] queryVector, int topK, float minScore, CancellationToken ct)
    {
        SearchCalls++;
        LastSearch = (queryVector, topK, minScore);

        if (FailSearch)
        {
            return Task.FromResult(Result<IReadOnlyList<RetrievedChunk>>.Failure(
                "VectorSearchFailed: vector store unavailable.", "VectorSearchFailed"));
        }

        IReadOnlyList<RetrievedChunk> hits =
        [
            .. _points.Values
                .Select(p => new RetrievedChunk(
                    p.Chunk.SourceId,
                    p.Chunk.SourceType,
                    p.Chunk.Title,
                    p.Chunk.Index,
                    p.Chunk.Text,
                    VectorMath.CosineSimilarity(queryVector, p.Vector)))
                .Where(c => c.Score >= minScore)
                .OrderByDescending(c => c.Score)
                .Take(topK)
        ];

        return Task.FromResult(Result<IReadOnlyList<RetrievedChunk>>.Success(hits));
    }
}
