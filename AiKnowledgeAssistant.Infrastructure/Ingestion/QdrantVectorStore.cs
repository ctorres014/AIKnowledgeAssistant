using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Qdrant-backed vector index. A thin adapter over <see cref="QdrantClient"/>: the point and filter
/// shapes live in <see cref="QdrantPayload"/>.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly IQdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;
    private readonly TimeProvider _time;
    private readonly string _collection;
    private readonly string _distance;
    private readonly int _dimension;

    public QdrantVectorStore(
        IQdrantClient client,
        IOptions<VectorStoreOptions> vectorStoreOptions,
        IOptions<EmbeddingOptions> embeddingOptions,
        ILogger<QdrantVectorStore> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _collection = vectorStoreOptions.Value.CollectionName;
        _distance = vectorStoreOptions.Value.Distance;
        _dimension = embeddingOptions.Value.Dimension;
    }

    public async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var distance = QdrantPayload.ParseDistance(_distance);

        if (await _client.CollectionExistsAsync(_collection, ct))
        {
            await VerifyDimensionAsync(ct);
        }
        else
        {
            _logger.LogInformation(
                "Creating Qdrant collection {Collection} with dimension {Dimension} and distance {Distance}",
                _collection, _dimension, distance);

            await _client.CreateCollectionAsync(
                _collection,
                new VectorParams { Size = (ulong)_dimension, Distance = distance },
                cancellationToken: ct);
        }

        foreach (var field in QdrantPayload.IndexedFields)
        {
            await _client.CreatePayloadIndexAsync(
                _collection, field, PayloadSchemaType.Keyword, cancellationToken: ct);
        }
    }

    public async Task<bool> ExistsWithHashAsync(string sourceId, string contentHash, CancellationToken ct)
    {
        var response = await _client.ScrollAsync(
            _collection,
            QdrantPayload.BySourceIdAndHash(sourceId, contentHash),
            limit: 1,
            payloadSelector: false,
            vectorsSelector: false,
            cancellationToken: ct);

        return response.Result.Count > 0;
    }

    public Task DeleteBySourceIdAsync(string sourceId, CancellationToken ct) =>
        _client.DeleteAsync(_collection, QdrantPayload.BySourceId(sourceId), cancellationToken: ct);

    public Task UpsertAsync(IReadOnlyList<(DocumentChunk Chunk, float[] Vector)> points, CancellationToken ct)
    {
        if (points.Count == 0)
        {
            return Task.CompletedTask;
        }

        var ingestedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();

        var structs = points
            .Select(p => QdrantPayload.ToPoint(p.Chunk, p.Vector, ingestedAt))
            .ToList();

        return _client.UpsertAsync(_collection, structs, cancellationToken: ct);
    }

    public async Task<VectorStoreStats> GetStatsAsync(CancellationToken ct)
    {
        var info = await _client.GetCollectionInfoAsync(_collection, ct);

        return new VectorStoreStats(_collection, (long)info.PointsCount, ReadDimension(info) ?? _dimension);
    }

    public async Task<Result<IReadOnlyList<RetrievedChunk>>> SearchAsync(
        float[] queryVector, int topK, float minScore, CancellationToken ct)
    {
        try
        {
            // scoreThreshold is applied by Qdrant itself, so noisy hits never travel back over the wire.
            var points = await _client.SearchAsync(
                _collection,
                queryVector,
                limit: (ulong)topK,
                payloadSelector: true,
                scoreThreshold: minScore,
                cancellationToken: ct);

            return Result<IReadOnlyList<RetrievedChunk>>.Success(
                [.. points.Select(QdrantPayload.ToRetrievedChunk)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector search failed against collection {Collection}", _collection);

            return Result<IReadOnlyList<RetrievedChunk>>.Failure(
                $"VectorSearchFailed: {ex.Message}", "VectorSearchFailed");
        }
    }

    /// <summary>
    /// Refuses to run against a collection built for a different embedding model: mixing vector
    /// spaces silently ruins retrieval, so the startup path fails loudly instead.
    /// </summary>
    private async Task VerifyDimensionAsync(CancellationToken ct)
    {
        var info = await _client.GetCollectionInfoAsync(_collection, ct);
        var existing = ReadDimension(info);

        if (existing is not null && existing != _dimension)
        {
            throw new InvalidOperationException(
                $"Qdrant collection '{_collection}' has dimension {existing} but the configured embedding " +
                $"model produces {_dimension}. Recreate the collection and re-ingest, or restore the " +
                "previous Embeddings:Model / Embeddings:Dimension configuration.");
        }
    }

    private static int? ReadDimension(CollectionInfo info)
    {
        var parameters = info.Config?.Params?.VectorsConfig?.Params;

        return parameters is null ? null : (int)parameters.Size;
    }
}
