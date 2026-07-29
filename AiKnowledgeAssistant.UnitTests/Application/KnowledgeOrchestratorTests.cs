using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;
using AiKnowledgeAssistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.UnitTests.Application;

public class KnowledgeOrchestratorTests
{
    private const string Question = "¿Cuánto dura el onboarding?";

    private static RetrievedChunk Chunk() =>
        new("c:/docs/handbook.md", SourceType.Markdown, "handbook", 3, "Texto de handbook.", 0.82f);

    private static KnowledgeOrchestrator CreateOrchestrator(
        InMemoryVectorStore vectorStore,
        FakeLlmClient llm,
        FakeEmbeddingGenerator? embeddings = null)
    {
        var pipeline = new RagPipeline(
            embeddings ?? new FakeEmbeddingGenerator(),
            vectorStore,
            llm,
            new GroundedPromptBuilder(),
            Options.Create(new RagOptions()),
            NullLogger<RagPipeline>.Instance);

        return new KnowledgeOrchestrator(pipeline, NullLogger<KnowledgeOrchestrator>.Instance);
    }

    [Fact]
    public async Task AskAsync_ReturnsThePipelineAnswerUnchanged()
    {
        var chunks = new[] { Chunk() };
        var orchestrator = CreateOrchestrator(
            new InMemoryVectorStore { SearchResults = chunks },
            new FakeLlmClient("El onboarding dura cinco jornadas.", model: "llama3.2:3b"));

        var result = await orchestrator.AskAsync(Question, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.FoundAnswer);
        Assert.Equal("El onboarding dura cinco jornadas.", result.Value.Text);
        Assert.Equal(chunks, result.Value.Citations);
        Assert.Equal("llama3.2:3b", result.Value.Model);
    }

    [Fact]
    public async Task AskAsync_ReachesThePipelineOnEveryCall_BecauseThereIsNoCacheUntilSpec04()
    {
        var llm = new FakeLlmClient();
        var orchestrator = CreateOrchestrator(new InMemoryVectorStore { SearchResults = [Chunk()] }, llm);

        await orchestrator.AskAsync(Question, CancellationToken.None);
        await orchestrator.AskAsync(Question, CancellationToken.None);

        Assert.Equal(2, llm.Calls);
    }

    [Fact]
    public async Task AskAsync_PassesThroughTheNoResultsAnswer()
    {
        var llm = new FakeLlmClient();
        var orchestrator = CreateOrchestrator(new InMemoryVectorStore { SearchResults = [] }, llm);

        var result = await orchestrator.AskAsync(Question, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.FoundAnswer);
        Assert.Null(result.Value.Text);
        Assert.Empty(result.Value.Citations);
        Assert.Equal(0, llm.Calls);
    }

    [Theory]
    [InlineData("LlmUnavailable")]
    [InlineData("LlmTimeout")]
    public async Task AskAsync_PropagatesAGenerationFailureWithItsCode(string code)
    {
        var orchestrator = CreateOrchestrator(
            new InMemoryVectorStore { SearchResults = [Chunk()] },
            new FakeLlmClient { FailWith = code });

        var result = await orchestrator.AskAsync(Question, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task AskAsync_PropagatesASearchFailureWithItsCode()
    {
        var orchestrator = CreateOrchestrator(
            new InMemoryVectorStore { FailSearch = true }, new FakeLlmClient());

        var result = await orchestrator.AskAsync(Question, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VectorSearchFailed", result.ErrorCode);
    }

    [Fact]
    public async Task AskAsync_PropagatesAnEmbeddingFailureWithItsCode()
    {
        var orchestrator = CreateOrchestrator(
            new InMemoryVectorStore(),
            new FakeLlmClient(),
            new FakeEmbeddingGenerator { FailAlways = true });

        var result = await orchestrator.AskAsync(Question, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingRequestFailed", result.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AskAsync_RejectsABlankQuestion(string question)
    {
        var orchestrator = CreateOrchestrator(new InMemoryVectorStore(), new FakeLlmClient());

        await Assert.ThrowsAsync<ArgumentException>(
            () => orchestrator.AskAsync(question, CancellationToken.None));
    }
}
