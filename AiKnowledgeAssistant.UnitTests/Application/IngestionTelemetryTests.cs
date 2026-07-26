using System.Diagnostics;
using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.UnitTests.Application;

/// <summary>
/// Asserts the trace the pipeline emits. The Aspire dashboard is only a viewer: what matters is that
/// the activity source produces one span per stage, correctly parented and tagged.
/// </summary>
public class IngestionTelemetryTests : IDisposable
{
    /// <summary>
    /// Wraps each run so its spans share one trace id. The listener is process-wide, and other test
    /// classes ingest in parallel — without this scoping their spans would be counted here too.
    /// </summary>
    private const string TestScopeName = "AiKnowledgeAssistant.Tests.TelemetryScope";

    private static readonly ActivitySource TestScope = new(TestScopeName);

    private readonly List<Activity> _captured = [];
    private readonly ActivityListener _listener;
    private readonly InMemoryVectorStore _store = new();

    private ActivityTraceId _traceId;

    public IngestionTelemetryTests()
    {
        _listener = new ActivityListener
        {
            // Must not touch TestScope: constructing an ActivitySource runs this predicate, and the
            // field is still being initialized at that point.
            ShouldListenTo = source =>
                source.Name == IngestionTelemetry.ActivitySourceName || source.Name == TestScopeName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (_captured)
                {
                    _captured.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>Runs an ingestion inside a fresh trace and remembers which trace to assert on.</summary>
    private async Task Ingest(IngestDocumentsHandler handler, IngestDocumentsCommand command)
    {
        using var root = TestScope.StartActivity("test-scope");
        Assert.NotNull(root);
        _traceId = root.TraceId;

        await handler.HandleAsync(command, CancellationToken.None);
    }

    /// <summary>Spans of the run under test only.</summary>
    private IReadOnlyList<Activity> Captured
    {
        get
        {
            lock (_captured)
            {
                return [.. _captured.Where(a => a.TraceId == _traceId)];
            }
        }
    }

    private IngestDocumentsHandler CreateHandler(
        FakeDocumentSource source,
        string[] paths,
        FakeEmbeddingGenerator? embeddings = null) =>
        new(
            FakeDocumentLocator.Returning(source, paths),
            new FixedWindowTextChunker(Options.Create(new ChunkingOptions())),
            embeddings ?? new FakeEmbeddingGenerator(),
            _store,
            Options.Create(new IngestionOptions()),
            NullLogger<IngestDocumentsHandler>.Instance);

    private Activity Span(string name) => Assert.Single(Captured, a => a.OperationName == name);

    private IReadOnlyList<Activity> Spans(string name) =>
        [.. Captured.Where(a => a.OperationName == name)];

    [Fact]
    public async Task Ingestion_EmitsOneSpanPerStage()
    {
        var source = new FakeDocumentSource().WithDocument("c:/docs/a.md", "# A\n\nSome policy text.");
        var handler = CreateHandler(source, ["c:/docs/a.md"]);

        await Ingest(handler, new IngestDocumentsCommand("c:/docs", null));

        Assert.NotNull(Span(IngestionTelemetry.BatchSpan));
        Assert.NotNull(Span(IngestionTelemetry.DocumentSpan));
        Assert.NotNull(Span(IngestionTelemetry.ExtractSpan));
        Assert.NotNull(Span(IngestionTelemetry.ChunkSpan));
        Assert.NotNull(Span(IngestionTelemetry.EmbedSpan));
        Assert.NotNull(Span(IngestionTelemetry.UpsertSpan));
    }

    [Fact]
    public async Task Ingestion_NestsTheStagesUnderTheDocumentAndTheBatch()
    {
        var source = new FakeDocumentSource().WithDocument("c:/docs/a.md", "# A\n\nSome policy text.");
        var handler = CreateHandler(source, ["c:/docs/a.md"]);

        await Ingest(handler, new IngestDocumentsCommand("c:/docs", null));

        var batch = Span(IngestionTelemetry.BatchSpan);
        var document = Span(IngestionTelemetry.DocumentSpan);

        Assert.Equal(batch.SpanId, document.ParentSpanId);

        foreach (var stage in new[]
                 {
                     IngestionTelemetry.ExtractSpan, IngestionTelemetry.ChunkSpan,
                     IngestionTelemetry.EmbedSpan, IngestionTelemetry.UpsertSpan
                 })
        {
            Assert.Equal(document.SpanId, Span(stage).ParentSpanId);
        }
    }

    [Fact]
    public async Task BatchSpan_CarriesTheRequestAndTheOutcome()
    {
        var source = new FakeDocumentSource()
            .WithDocument("c:/docs/a.md", "# A\n\nText.")
            .WithDocument("c:/docs/b.md", "# B\n\nText.");

        var handler = CreateHandler(source, ["c:/docs/a.md", "c:/docs/b.md"]);

        await Ingest(handler, new IngestDocumentsCommand("c:/docs", "markdown"));

        var batch = Span(IngestionTelemetry.BatchSpan);

        Assert.Equal("c:/docs", batch.GetTagItem("ingestion.path"));
        Assert.Equal("markdown", batch.GetTagItem("ingestion.source_type"));
        Assert.Equal(2, batch.GetTagItem("ingestion.total_files"));
        Assert.Equal("Completed", batch.GetTagItem("ingestion.status"));
        Assert.Equal(2, batch.GetTagItem("ingestion.ingested"));
        Assert.Equal(0, batch.GetTagItem("ingestion.remaining"));
    }

    [Fact]
    public async Task DocumentSpan_IsEmittedPerDocumentWithItsOutcome()
    {
        var source = new FakeDocumentSource()
            .WithDocument("c:/docs/a.md", "# A\n\nText.")
            .WithFailure("c:/docs/b.md", "PdfExtractionFailed: broken");

        var handler = CreateHandler(source, ["c:/docs/a.md", "c:/docs/b.md"]);

        await Ingest(handler, new IngestDocumentsCommand("c:/docs", null));

        var documents = Spans(IngestionTelemetry.DocumentSpan);
        Assert.Equal(2, documents.Count);

        var ingestedSpan = Assert.Single(documents, d => (string?)d.GetTagItem("ingestion.outcome") == "Ingested");
        Assert.Equal(ActivityStatusCode.Unset, ingestedSpan.Status);

        var failedSpan = Assert.Single(documents, d => (string?)d.GetTagItem("ingestion.outcome") == "Failed");
        Assert.Equal(ActivityStatusCode.Error, failedSpan.Status);
        Assert.Contains("PdfExtractionFailed", failedSpan.StatusDescription!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkippedDocument_StopsAfterTheExtractStage()
    {
        var source = new FakeDocumentSource().WithDocument("c:/docs/a.md", "# A\n\nText.");

        // First pass indexes it; second pass must skip it.
        await Ingest(CreateHandler(source, ["c:/docs/a.md"]), new IngestDocumentsCommand("c:/docs", null));

        // A second run in its own trace: only that trace is asserted on.
        await Ingest(CreateHandler(source, ["c:/docs/a.md"]), new IngestDocumentsCommand("c:/docs", null));

        Assert.Equal("Skipped", Span(IngestionTelemetry.DocumentSpan).GetTagItem("ingestion.outcome"));
        Assert.NotNull(Span(IngestionTelemetry.ExtractSpan));
        Assert.Empty(Spans(IngestionTelemetry.EmbedSpan));
        Assert.Empty(Spans(IngestionTelemetry.UpsertSpan));
    }

    [Fact]
    public async Task EmbeddingFailure_MarksTheEmbedSpanAsError()
    {
        var source = new FakeDocumentSource().WithDocument("c:/docs/a.md", "# A\n\nText.");
        var handler = CreateHandler(source, ["c:/docs/a.md"], new FakeEmbeddingGenerator { FailAlways = true });

        await Ingest(handler, new IngestDocumentsCommand("c:/docs", null));

        var embed = Span(IngestionTelemetry.EmbedSpan);

        Assert.Equal(ActivityStatusCode.Error, embed.Status);
        Assert.Empty(Spans(IngestionTelemetry.UpsertSpan));
    }

    [Fact]
    public async Task RejectedBatch_MarksTheBatchSpanAsError()
    {
        var source = new FakeDocumentSource().WithDocument("c:/docs/a.md", "# A\n\nText.");
        var handler = new IngestDocumentsHandler(
            FakeDocumentLocator.Returning(source, "c:/docs/a.md", "c:/docs/b.md"),
            new FixedWindowTextChunker(Options.Create(new ChunkingOptions())),
            new FakeEmbeddingGenerator(),
            _store,
            Options.Create(new IngestionOptions { MaxFilesPerRequest = 1 }),
            NullLogger<IngestDocumentsHandler>.Instance);

        await Ingest(handler, new IngestDocumentsCommand("c:/docs", null));

        var batch = Span(IngestionTelemetry.BatchSpan);

        Assert.Equal(ActivityStatusCode.Error, batch.Status);
        Assert.Equal("TooManyFiles", batch.StatusDescription);
        Assert.Empty(Spans(IngestionTelemetry.DocumentSpan));
    }

    [Fact]
    public void ActivitySourceAndMeter_UseTheNameFromTheSpec()
    {
        Assert.Equal("AiKnowledgeAssistant.Ingestion", IngestionTelemetry.ActivitySourceName);
        Assert.Equal("AiKnowledgeAssistant.Ingestion", IngestionTelemetry.MeterName);
    }
}
