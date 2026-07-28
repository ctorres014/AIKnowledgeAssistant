using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;
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

    /// <summary>
    /// A scored search hit back into the domain. Five payload fields plus the score; the point ID and
    /// <c>contentHash</c> are ingestion bookkeeping that a citation has no use for.
    /// </summary>
    public static RetrievedChunk ToRetrievedChunk(ScoredPoint point) => new(
        ReadString(point.Payload, SourceIdField),
        ReadSourceType(point.Payload),
        ReadString(point.Payload, TitleField),
        (int)ReadInteger(point.Payload, ChunkIndexField),
        ReadString(point.Payload, TextField),
        point.Score);

    private static string ReadString(IDictionary<string, Value> payload, string field) =>
        payload.TryGetValue(field, out var value) ? value.StringValue : string.Empty;

    private static long ReadInteger(IDictionary<string, Value> payload, string field) =>
        payload.TryGetValue(field, out var value) ? value.IntegerValue : 0;

    /// <summary>
    /// Ingestion writes this field from the enum itself, so an unparsable value means the collection
    /// was written by something else. The citation degrades to <see cref="SourceType.Text"/> instead of
    /// failing a query that is otherwise perfectly answerable.
    /// </summary>
    private static SourceType ReadSourceType(IDictionary<string, Value> payload) =>
        Enum.TryParse<SourceType>(ReadString(payload, SourceTypeField), ignoreCase: true, out var parsed)
            ? parsed
            : SourceType.Text;

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
