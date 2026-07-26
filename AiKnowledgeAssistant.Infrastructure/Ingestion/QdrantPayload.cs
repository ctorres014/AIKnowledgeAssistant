using AiKnowledgeAssistant.Domain.Ingestion;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Translation between domain chunks and Qdrant points/filters. Kept separate from the store so the
/// payload shape and the idempotency filters can be asserted without a live Qdrant.
/// </summary>
/// <remarks>
/// This payload is the only ingestion tracking in SPEC 02 — there is no relational store yet, so
/// <c>sourceId</c> and <c>contentHash</c> carry the dedup decisions and are the two indexed fields.
/// </remarks>
public static class QdrantPayload
{
    public const string SourceIdField = "sourceId";
    public const string SourceTypeField = "sourceType";
    public const string TitleField = "title";
    public const string ChunkIndexField = "chunkIndex";
    public const string TextField = "text";
    public const string ContentHashField = "contentHash";
    public const string IngestedAtField = "ingestedAt";

    /// <summary>Payload fields indexed in Qdrant: the only two the pipeline filters on.</summary>
    public static IReadOnlyList<string> IndexedFields { get; } = [SourceIdField, ContentHashField];

    /// <summary>The seven payload fields of a chunk.</summary>
    public static Dictionary<string, Value> Build(DocumentChunk chunk, long ingestedAtUnixMs) => new()
    {
        [SourceIdField] = chunk.SourceId,
        [SourceTypeField] = chunk.SourceType.ToString(),
        [TitleField] = chunk.Title,
        [ChunkIndexField] = chunk.Index,
        [TextField] = chunk.Text,
        [ContentHashField] = chunk.ContentHash,
        [IngestedAtField] = ingestedAtUnixMs
    };

    /// <summary>
    /// A point whose ID is the chunk's deterministic GUID, so re-ingesting overwrites in place
    /// rather than accumulating duplicates.
    /// </summary>
    public static PointStruct ToPoint(DocumentChunk chunk, float[] vector, long ingestedAtUnixMs)
    {
        var point = new PointStruct
        {
            Id = chunk.Id,
            Vectors = vector
        };

        foreach (var (field, value) in Build(chunk, ingestedAtUnixMs))
        {
            point.Payload[field] = value;
        }

        return point;
    }

    /// <summary>Every chunk of one document — the delete filter used before reindexing.</summary>
    public static Filter BySourceId(string sourceId) => MatchKeyword(SourceIdField, sourceId);

    /// <summary>One document at one exact content version — the skip-if-unchanged filter.</summary>
    public static Filter BySourceIdAndHash(string sourceId, string contentHash) => new()
    {
        Must =
        {
            MatchKeyword(SourceIdField, sourceId),
            MatchKeyword(ContentHashField, contentHash)
        }
    };

    /// <summary>Maps the configured distance name onto the gRPC enum, rejecting unknown values loudly.</summary>
    public static Distance ParseDistance(string distance) => distance?.Trim().ToLowerInvariant() switch
    {
        "cosine" => Distance.Cosine,
        "dot" => Distance.Dot,
        "euclid" or "euclidean" => Distance.Euclid,
        "manhattan" => Distance.Manhattan,
        _ => throw new ArgumentOutOfRangeException(
            nameof(distance), distance, "Unsupported vector distance; use Cosine, Dot, Euclid or Manhattan.")
    };
}
