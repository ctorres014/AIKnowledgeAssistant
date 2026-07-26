using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>
/// Traces and metrics of the ingestion pipeline. Both names are registered with OpenTelemetry by
/// <c>AddIngestion</c>, so spans and counters show up in the Aspire dashboard.
/// </summary>
public static class IngestionTelemetry
{
    /// <summary>Name of the activity source, as registered with the tracer provider.</summary>
    public const string ActivitySourceName = "AiKnowledgeAssistant.Ingestion";

    /// <summary>Name of the meter, as registered with the meter provider.</summary>
    public const string MeterName = "AiKnowledgeAssistant.Ingestion";

    /// <summary>Span names emitted per stage of the pipeline.</summary>
    public const string BatchSpan = "ingest.batch";
    public const string DocumentSpan = "ingest.document";
    public const string ExtractSpan = "ingest.extract";
    public const string ChunkSpan = "ingest.chunk";
    public const string EmbedSpan = "ingest.embed";
    public const string UpsertSpan = "ingest.upsert";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static Counter<long> DocumentsIngested { get; } = Meter.CreateCounter<long>(
        "ingestion.documents.ingested", "{document}", "Documents chunked, embedded and indexed.");

    public static Counter<long> DocumentsSkipped { get; } = Meter.CreateCounter<long>(
        "ingestion.documents.skipped", "{document}", "Documents already indexed with the same content hash.");

    public static Counter<long> DocumentsFailed { get; } = Meter.CreateCounter<long>(
        "ingestion.documents.failed", "{document}", "Documents that could not be ingested.");

    public static Counter<long> ChunksIndexed { get; } = Meter.CreateCounter<long>(
        "ingestion.chunks.indexed", "{chunk}", "Chunks upserted into the vector store.");

    public static Histogram<double> BatchDuration { get; } = Meter.CreateHistogram<double>(
        "ingestion.batch.duration", "ms", "Wall-clock duration of an ingestion request.");
}
