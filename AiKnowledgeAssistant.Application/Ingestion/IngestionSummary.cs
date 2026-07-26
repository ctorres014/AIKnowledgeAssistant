using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>What an ingestion run did, file by file.</summary>
/// <param name="Status">
/// <see cref="IngestionStatus.Completed"/> when every discovered file was processed;
/// <see cref="IngestionStatus.PartiallyCompleted"/> when the run was cut and <paramref name="Remaining"/> is above zero.
/// </param>
/// <param name="TotalFiles">Files discovered under the requested path.</param>
/// <param name="Ingested">Files chunked, embedded and upserted in this run.</param>
/// <param name="Skipped">Files already indexed with the same content hash.</param>
/// <param name="FailedCount">Files that could not be ingested; see <paramref name="Failed"/>.</param>
/// <param name="Remaining">Files never attempted because the run was cut short.</param>
/// <param name="ChunksIndexed">Chunks upserted into the vector store.</param>
/// <param name="DurationMs">Wall-clock duration of the run.</param>
/// <param name="Failed">One entry per failed file, with the reason.</param>
public sealed record IngestionSummary(
    IngestionStatus Status,
    int TotalFiles,
    int Ingested,
    int Skipped,
    int FailedCount,
    int Remaining,
    int ChunksIndexed,
    long DurationMs,
    IReadOnlyList<IngestionFailure> Failed);

/// <summary>A file that could not be ingested, and why.</summary>
/// <param name="Path">The file that failed.</param>
/// <param name="Reason">Error code and detail, e.g. <c>PdfExtractionFailed: ...</c>.</param>
public sealed record IngestionFailure(string Path, string Reason);
