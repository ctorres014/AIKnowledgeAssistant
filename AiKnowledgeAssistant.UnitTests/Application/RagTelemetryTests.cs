using System.Diagnostics;
using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;
using AiKnowledgeAssistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.UnitTests.Application;

/// <summary>
/// Asserts the trace a query emits. Until SPEC 05 brings persistence this trace is the only record a
/// query leaves, so its shape and its tags are the whole diagnostic surface of the query path.
/// </summary>
public class RagTelemetryTests : IDisposable
{
    /// <summary>
    /// Wraps each run so its spans share one trace id. The listener is process-wide and other test
    /// classes query in parallel — without this scoping their spans would be counted here too.
    /// </summary>
    private const string TestScopeName = "AiKnowledgeAssistant.Tests.RagTelemetryScope";

    private static readonly ActivitySource TestScope = new(TestScopeName);

    private const string Question = "¿Cuánto dura el onboarding?";

    private readonly List<Activity> _captured = [];
    private readonly ActivityListener _listener;

    private ActivityTraceId _traceId;

    public RagTelemetryTests()
    {
        _listener = new ActivityListener
        {
            // Must not touch TestScope: constructing an ActivitySource runs this predicate, and the
            // field is still being initialized at that point.
            ShouldListenTo = source =>
                source.Name == RagTelemetry.ActivitySourceName || source.Name == TestScopeName,
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

    private static RetrievedChunk Chunk(string title, int index, float score) =>
        new($"c:/docs/{title}.md", SourceType.Markdown, title, index, $"Texto de {title}.", score);

    private static KnowledgeOrchestrator CreateOrchestrator(
        InMemoryVectorStore vectorStore,
        FakeLlmClient llm,
        FakeEmbeddingGenerator? embeddings = null,
        RagOptions? options = null) =>
        new(
            new RagPipeline(
                embeddings ?? new FakeEmbeddingGenerator(),
                vectorStore,
                llm,
                new GroundedPromptBuilder(),
                Options.Create(options ?? new RagOptions()),
                NullLogger<RagPipeline>.Instance),
            NullLogger<KnowledgeOrchestrator>.Instance);

    /// <summary>Runs a query inside a fresh trace and remembers which trace to assert on.</summary>
    private async Task Ask(KnowledgeOrchestrator orchestrator)
    {
        using var root = TestScope.StartActivity("test-scope");
        Assert.NotNull(root);
        _traceId = root.TraceId;

        await orchestrator.AskAsync(Question, CancellationToken.None);
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

    private Activity Span(string name) => Assert.Single(Captured, a => a.OperationName == name);

    private IReadOnlyList<Activity> Spans(string name) =>
        [.. Captured.Where(a => a.OperationName == name)];

    [Fact]
    public async Task Query_EmitsOneSpanPerStage()
    {
        var store = new InMemoryVectorStore { SearchResults = [Chunk("handbook", 3, 0.82f)] };

        await Ask(CreateOrchestrator(store, new FakeLlmClient()));

        Assert.NotNull(Span(RagTelemetry.QuerySpan));
        Assert.NotNull(Span(RagTelemetry.EmbedSpan));
        Assert.NotNull(Span(RagTelemetry.SearchSpan));
        Assert.NotNull(Span(RagTelemetry.GenerateSpan));
    }

    [Fact]
    public async Task Query_NestsTheStagesUnderTheQuerySpan()
    {
        var store = new InMemoryVectorStore { SearchResults = [Chunk("handbook", 3, 0.82f)] };

        await Ask(CreateOrchestrator(store, new FakeLlmClient()));

        var query = Span(RagTelemetry.QuerySpan);

        foreach (var stage in new[] { RagTelemetry.EmbedSpan, RagTelemetry.SearchSpan, RagTelemetry.GenerateSpan })
        {
            Assert.Equal(query.SpanId, Span(stage).ParentSpanId);
        }
    }

    [Fact]
    public async Task SearchSpan_CarriesTheRetrievalKnobsAndWhatCameBack()
    {
        var store = new InMemoryVectorStore
        {
            SearchResults = [Chunk("handbook", 3, 0.82f), Chunk("faq", 1, 0.61f)]
        };

        await Ask(CreateOrchestrator(store, new FakeLlmClient(), options: new RagOptions { TopK = 8, MinScore = 0.65f }));

        var search = Span(RagTelemetry.SearchSpan);

        Assert.Equal(8, search.GetTagItem("rag.top_k"));
        Assert.Equal(0.65f, search.GetTagItem("rag.min_score"));
        Assert.Equal(2, search.GetTagItem("rag.chunks_retrieved"));
        Assert.Equal(0.82f, search.GetTagItem("rag.max_score"));
    }

    [Fact]
    public async Task QuerySpan_CarriesTheOutcomeAndTheModel()
    {
        var store = new InMemoryVectorStore { SearchResults = [Chunk("handbook", 3, 0.82f)] };

        await Ask(CreateOrchestrator(store, new FakeLlmClient(model: "qwen2.5:7b")));

        var query = Span(RagTelemetry.QuerySpan);

        Assert.Equal("qwen2.5:7b", query.GetTagItem("rag.model"));
        Assert.Equal(true, query.GetTagItem("rag.found_answer"));
        Assert.Equal(1, query.GetTagItem("rag.citations"));
        Assert.Equal(ActivityStatusCode.Unset, query.Status);
    }

    [Fact]
    public async Task GenerateSpan_CarriesTheModelThatAnswered()
    {
        var store = new InMemoryVectorStore { SearchResults = [Chunk("handbook", 3, 0.82f)] };

        await Ask(CreateOrchestrator(store, new FakeLlmClient(model: "qwen2.5:7b")));

        Assert.Equal("qwen2.5:7b", Span(RagTelemetry.GenerateSpan).GetTagItem("rag.model"));
    }

    [Fact]
    public async Task QueryWithoutResults_StopsAfterTheSearchStage()
    {
        await Ask(CreateOrchestrator(new InMemoryVectorStore { SearchResults = [] }, new FakeLlmClient()));

        var query = Span(RagTelemetry.QuerySpan);

        Assert.Equal(false, query.GetTagItem("rag.found_answer"));
        Assert.Equal(0, query.GetTagItem("rag.citations"));
        Assert.Equal(0, Span(RagTelemetry.SearchSpan).GetTagItem("rag.chunks_retrieved"));
        Assert.Null(Span(RagTelemetry.SearchSpan).GetTagItem("rag.max_score"));
        Assert.Empty(Spans(RagTelemetry.GenerateSpan));
    }

    [Fact]
    public async Task EmbeddingFailure_MarksTheEmbedAndQuerySpansAsError()
    {
        var orchestrator = CreateOrchestrator(
            new InMemoryVectorStore(),
            new FakeLlmClient(),
            new FakeEmbeddingGenerator { FailAlways = true });

        await Ask(orchestrator);

        Assert.Equal(ActivityStatusCode.Error, Span(RagTelemetry.EmbedSpan).Status);

        var query = Span(RagTelemetry.QuerySpan);
        Assert.Equal(ActivityStatusCode.Error, query.Status);
        Assert.Equal("EmbeddingRequestFailed", query.GetTagItem("rag.error_code"));

        Assert.Empty(Spans(RagTelemetry.SearchSpan));
        Assert.Empty(Spans(RagTelemetry.GenerateSpan));
    }

    [Fact]
    public async Task SearchFailure_MarksTheSearchSpanAsErrorAndNeverGenerates()
    {
        await Ask(CreateOrchestrator(new InMemoryVectorStore { FailSearch = true }, new FakeLlmClient()));

        Assert.Equal(ActivityStatusCode.Error, Span(RagTelemetry.SearchSpan).Status);
        Assert.Equal("VectorSearchFailed", Span(RagTelemetry.QuerySpan).GetTagItem("rag.error_code"));
        Assert.Empty(Spans(RagTelemetry.GenerateSpan));
    }

    [Theory]
    [InlineData("LlmUnavailable")]
    [InlineData("LlmTimeout")]
    public async Task GenerationFailure_MarksTheGenerateSpanAsErrorWithItsCode(string code)
    {
        var store = new InMemoryVectorStore { SearchResults = [Chunk("handbook", 3, 0.82f)] };

        await Ask(CreateOrchestrator(store, new FakeLlmClient { FailWith = code }));

        Assert.Equal(ActivityStatusCode.Error, Span(RagTelemetry.GenerateSpan).Status);

        var query = Span(RagTelemetry.QuerySpan);
        Assert.Equal(ActivityStatusCode.Error, query.Status);
        Assert.Equal(code, query.GetTagItem("rag.error_code"));
    }

    [Fact]
    public void ActivitySourceAndMeter_UseTheNameFromTheSpec()
    {
        Assert.Equal("AiKnowledgeAssistant.Rag", RagTelemetry.ActivitySourceName);
        Assert.Equal("AiKnowledgeAssistant.Rag", RagTelemetry.MeterName);
    }
}
