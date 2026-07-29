using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;
using AiKnowledgeAssistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.UnitTests.Application;

public class RagPipelineTests
{
    private const string Question = "¿Cuánto dura el onboarding?";

    private static RetrievedChunk Chunk(string title = "handbook", int index = 3, float score = 0.82f) =>
        new($"c:/docs/{title}.md", SourceType.Markdown, title, index, $"Texto de {title}.", score);

    private static RagPipeline CreatePipeline(
        InMemoryVectorStore vectorStore,
        FakeLlmClient llm,
        FakeEmbeddingGenerator? embeddings = null,
        RagOptions? options = null) =>
        new(
            embeddings ?? new FakeEmbeddingGenerator(),
            vectorStore,
            llm,
            new GroundedPromptBuilder(),
            Options.Create(options ?? new RagOptions()),
            NullLogger<RagPipeline>.Instance);

    [Fact]
    public async Task AnswerAsync_WithoutChunksAboveTheThreshold_NeverCallsTheLlm()
    {
        var llm = new FakeLlmClient();
        var pipeline = CreatePipeline(new InMemoryVectorStore { SearchResults = [] }, llm);

        var result = await pipeline.AnswerAsync(Question, CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.FoundAnswer);
        Assert.Null(result.Value.Text);
        Assert.Empty(result.Value.Citations);
    }

    [Fact]
    public async Task AnswerAsync_WithoutChunks_StillReportsTheConfiguredModel()
    {
        var llm = new FakeLlmClient(model: "qwen2.5:7b");
        var pipeline = CreatePipeline(new InMemoryVectorStore { SearchResults = [] }, llm);

        var result = await pipeline.AnswerAsync(Question, CancellationToken.None);

        Assert.Equal("qwen2.5:7b", result.Value!.Model);
    }

    [Fact]
    public async Task AnswerAsync_WithChunks_ReturnsTheGeneratedTextAndOneCitationPerChunk()
    {
        var chunks = new[] { Chunk("handbook", 0), Chunk("policies", 7), Chunk("faq", 2) };
        var llm = new FakeLlmClient("El onboarding dura cinco jornadas.");
        var pipeline = CreatePipeline(new InMemoryVectorStore { SearchResults = chunks }, llm);

        var result = await pipeline.AnswerAsync(Question, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.FoundAnswer);
        Assert.Equal("El onboarding dura cinco jornadas.", result.Value.Text);
        Assert.Equal(3, result.Value.Citations.Count);
        Assert.Equal(chunks, result.Value.Citations);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task AnswerAsync_GroundsThePromptOnTheRetrievedChunksAndTheQuestion()
    {
        var llm = new FakeLlmClient();
        var pipeline = CreatePipeline(new InMemoryVectorStore { SearchResults = [Chunk("handbook")] }, llm);

        await pipeline.AnswerAsync(Question, CancellationToken.None);

        var prompt = Assert.Single(llm.Prompts);
        Assert.Equal(GroundedPromptBuilder.SystemPrompt, prompt.System);
        Assert.Contains("Texto de handbook.", prompt.User, StringComparison.Ordinal);
        Assert.Contains(Question, prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerAsync_AsksTheStoreForTheConfiguredTopKAndMinScore()
    {
        var store = new InMemoryVectorStore { SearchResults = [] };
        var options = new RagOptions { TopK = 8, MinScore = 0.65f };

        await CreatePipeline(store, new FakeLlmClient(), options: options)
            .AnswerAsync(Question, CancellationToken.None);

        Assert.Equal(1, store.SearchCalls);
        Assert.Equal(8, store.LastSearch!.Value.TopK);
        Assert.Equal(0.65f, store.LastSearch.Value.MinScore);
        Assert.Equal(768, store.LastSearch.Value.Vector.Length);
    }

    [Fact]
    public async Task AnswerAsync_PropagatesAnEmbeddingFailureWithoutTouchingTheStore()
    {
        var store = new InMemoryVectorStore();
        var llm = new FakeLlmClient();
        var embeddings = new FakeEmbeddingGenerator { FailAlways = true };

        var result = await CreatePipeline(store, llm, embeddings).AnswerAsync(Question, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingRequestFailed", result.ErrorCode);
        Assert.Equal(0, store.SearchCalls);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task AnswerAsync_PropagatesASearchFailureWithoutCallingTheLlm()
    {
        var llm = new FakeLlmClient();

        var result = await CreatePipeline(new InMemoryVectorStore { FailSearch = true }, llm)
            .AnswerAsync(Question, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VectorSearchFailed", result.ErrorCode);
        Assert.Equal(0, llm.Calls);
    }

    [Theory]
    [InlineData("LlmUnavailable")]
    [InlineData("LlmTimeout")]
    public async Task AnswerAsync_PropagatesAGenerationFailure(string code)
    {
        var llm = new FakeLlmClient { FailWith = code };

        var result = await CreatePipeline(new InMemoryVectorStore { SearchResults = [Chunk()] }, llm)
            .AnswerAsync(Question, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnswerAsync_RejectsABlankQuestion(string question)
    {
        var pipeline = CreatePipeline(new InMemoryVectorStore(), new FakeLlmClient());

        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.AnswerAsync(question, CancellationToken.None));
    }
}
