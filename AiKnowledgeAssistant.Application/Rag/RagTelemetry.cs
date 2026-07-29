using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AiKnowledgeAssistant.Application.Rag;

/// <summary>
/// Traces and metrics of the query path. Both names are registered with OpenTelemetry by
/// <c>AddRag</c>, so spans and counters show up in the Aspire dashboard.
/// </summary>
/// <remarks>
/// Until SPEC 05 brings persistence, this is the <em>only</em> record a query leaves behind: the
/// question, how many chunks were retrieved, the best score and the model that answered are what
/// makes a bad answer diagnosable from the dashboard while the process is still running.
/// </remarks>
public static class RagTelemetry
{
    /// <summary>Name of the activity source, as registered with the tracer provider.</summary>
    public const string ActivitySourceName = "AiKnowledgeAssistant.Rag";

    /// <summary>Name of the meter, as registered with the meter provider.</summary>
    public const string MeterName = "AiKnowledgeAssistant.Rag";

    /// <summary>Span names emitted per stage of the query.</summary>
    public const string QuerySpan = "query";
    public const string EmbedSpan = "query.embed";
    public const string SearchSpan = "query.search";
    public const string GenerateSpan = "query.generate";

    /// <summary>Values of the <c>rag.stage</c> tag on <see cref="StageDuration"/>.</summary>
    public const string EmbedStage = "embed";
    public const string SearchStage = "search";
    public const string GenerateStage = "generate";
    public const string TotalStage = "total";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static Counter<long> QueriesAnswered { get; } = Meter.CreateCounter<long>(
        "rag.queries.answered", "{query}", "Questions answered from retrieved context.");

    public static Counter<long> QueriesWithoutResults { get; } = Meter.CreateCounter<long>(
        "rag.queries.no_results", "{query}", "Questions where no chunk cleared the score threshold.");

    public static Counter<long> QueriesFailed { get; } = Meter.CreateCounter<long>(
        "rag.queries.failed", "{query}", "Questions that could not be answered: embedding, search or generation failed.");

    /// <summary>
    /// Wall-clock duration per stage, tagged with <c>rag.stage</c> (<c>embed</c>, <c>search</c>,
    /// <c>generate</c>, <c>total</c>). One instrument rather than four keeps the stages comparable
    /// in a single dashboard chart.
    /// </summary>
    public static Histogram<double> StageDuration { get; } = Meter.CreateHistogram<double>(
        "rag.query.duration", "ms", "Wall-clock duration of a query stage.");

    /// <summary>Records <paramref name="elapsed"/> against <paramref name="stage"/>.</summary>
    public static void RecordStage(string stage, TimeSpan elapsed) =>
        StageDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("rag.stage", stage));
}
