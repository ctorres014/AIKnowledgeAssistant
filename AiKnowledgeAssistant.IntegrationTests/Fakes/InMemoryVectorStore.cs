using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.IntegrationTests.Fakes;

/// <summary>
/// In-memory stand-in for Qdrant, keyed by point ID exactly like the real store, so the endpoint
/// tests exercise real idempotency without a container runtime.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<Guid, (DocumentChunk Chunk, float[] Vector)> _points = [];
    private readonly Lock _gate = new();

    public string Collection => "knowledge";

    public int Dimension => 768;

    public int EnsureCollectionCalls { get; private set; }

    public long VectorsCount
    {
        get
        {
            lock (_gate)
            {
                return _points.Count;
            }
        }
    }

    /// <summary>Chunks currently indexed for one document, in order.</summary>
    public IReadOnlyList<DocumentChunk> ChunksOf(string sourceId)
    {
        lock (_gate)
        {
            return
            [
                .. _points.Values
                    .Select(p => p.Chunk)
                    .Where(c => string.Equals(c.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.Index)
            ];
        }
    }

    /// <summary>Distinct documents currently indexed.</summary>
    public IReadOnlyCollection<string> SourceIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _points.Values.Select(p => p.Chunk.SourceId).Distinct(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    public Task EnsureCollectionAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureCollectionCalls++;
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsWithHashAsync(string sourceId, string contentHash, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_points.Values.Any(p =>
                string.Equals(p.Chunk.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
                p.Chunk.ContentHash == contentHash));
        }
    }

    public Task DeleteBySourceIdAsync(string sourceId, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var id in _points
                .Where(p => string.Equals(p.Value.Chunk.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key)
                .ToArray())
            {
                _points.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task UpsertAsync(IReadOnlyList<(DocumentChunk Chunk, float[] Vector)> points, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var point in points)
            {
                _points[point.Chunk.Id] = point;
            }
        }

        return Task.CompletedTask;
    }

    public Task<VectorStoreStats> GetStatsAsync(CancellationToken ct) =>
        Task.FromResult(new VectorStoreStats(Collection, VectorsCount, Dimension));
}
