using System.Net;
using System.Net.Http.Json;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.IntegrationTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiKnowledgeAssistant.IntegrationTests;

/// <summary>
/// End-to-end tests of <c>POST /api/query</c> over the real controller, orchestrator, pipeline and
/// prompt builder. The three outbound dependencies — Ollama's embeddings, Qdrant and the chat model —
/// are in-memory doubles, so the suite needs no container runtime.
/// </summary>
/// <remarks>
/// The index is populated through <c>POST /api/ingest</c> rather than by seeding the double, so each
/// query answers over chunks that the real extractor, chunker and store produced.
/// </remarks>
public class QueryEndpointTests
{
    private const string Question = "¿Cuánto dura el onboarding?";

    private static string Samples(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Samples", .. parts]);

    /// <summary>An app whose vector store, embedding provider and LLM are the supplied doubles.</summary>
    private static WebApplicationFactory<Program> CreateFactory(
        InMemoryVectorStore vectorStore,
        FakeLlmClient llm) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:qdrant", "Endpoint=http://localhost:6334");
            builder.UseSetting("Aspire:Qdrant:Client:DisableHealthChecks", "true");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IVectorStore>();
                services.AddSingleton<IVectorStore>(vectorStore);

                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator>(new FakeEmbeddingGenerator());

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(llm);
            });
        });

    /// <summary>Fills the index through the real ingestion endpoint, so the query has something to find.</summary>
    private static async Task Ingest(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/ingest", new { path = Samples("Markdown"), sourceType = "markdown" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(HttpStatusCode Status, QueryBody? Body)> Query(
        HttpClient client, string question = Question, bool includeChunks = false)
    {
        var response = await client.PostAsJsonAsync("/api/query", new { question, includeChunks });

        return response.StatusCode == HttpStatusCode.OK
            ? (response.StatusCode, await response.Content.ReadFromJsonAsync<QueryBody>())
            : (response.StatusCode, null);
    }

    [Fact]
    public async Task Post_Query_Over_A_Populated_Index_Answers_With_Citations()
    {
        var llm = new FakeLlmClient();
        await using var factory = CreateFactory(new InMemoryVectorStore(), llm);
        using var client = factory.CreateClient();

        await Ingest(client);

        var (status, body) = await Query(client);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(body);
        Assert.True(body.FoundAnswer);
        Assert.False(string.IsNullOrWhiteSpace(body.Answer));
        Assert.NotEmpty(body.Citations);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task Post_Query_Returns_The_Configured_Model_And_A_Duration()
    {
        var llm = new FakeLlmClient(model: "qwen2.5:7b");
        await using var factory = CreateFactory(new InMemoryVectorStore(), llm);
        using var client = factory.CreateClient();

        await Ingest(client);

        var (_, body) = await Query(client);

        Assert.Equal("qwen2.5:7b", body!.Model);
        Assert.True(body.DurationMs >= 0);
    }

    [Fact]
    public async Task Post_Query_Citations_Carry_Their_Metadata_But_Not_The_Chunk_Text()
    {
        await using var factory = CreateFactory(new InMemoryVectorStore(), new FakeLlmClient());
        using var client = factory.CreateClient();

        await Ingest(client);

        var (_, body) = await Query(client);

        var citation = body!.Citations[0];

        Assert.False(string.IsNullOrWhiteSpace(citation.Title));
        Assert.False(string.IsNullOrWhiteSpace(citation.SourceId));
        Assert.Equal("Markdown", citation.SourceType);
        Assert.True(citation.ChunkIndex >= 0);
        Assert.True(citation.Score > 0);
        Assert.Null(citation.Text);
    }

    [Fact]
    public async Task Post_Query_With_IncludeChunks_Returns_The_Citation_Text()
    {
        await using var factory = CreateFactory(new InMemoryVectorStore(), new FakeLlmClient());
        using var client = factory.CreateClient();

        await Ingest(client);

        var (status, body) = await Query(client, includeChunks: true);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.All(body!.Citations, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    /// <summary>
    /// The contract's whole point: no context means a plain "I don't know" and, above all, no
    /// generation — the most expensive call in the pipeline is skipped, not spent saying nothing.
    /// </summary>
    [Fact]
    public async Task Post_Query_Without_Matches_Answers_Nothing_And_Never_Calls_The_Llm()
    {
        var llm = new FakeLlmClient();

        // Empty index: the search returns nothing at all, let alone anything above MinScore.
        await using var factory = CreateFactory(new InMemoryVectorStore(), llm);
        using var client = factory.CreateClient();

        var (status, body) = await Query(client);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(body);
        Assert.False(body.FoundAnswer);
        Assert.Null(body.Answer);
        Assert.Empty(body.Citations);
        Assert.Equal("llama3.2:3b", body.Model);
        Assert.Equal(0, llm.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_Query_With_A_Blank_Question_Returns_400(string question)
    {
        var llm = new FakeLlmClient();
        await using var factory = CreateFactory(new InMemoryVectorStore(), llm);
        using var client = factory.CreateClient();

        var (status, _) = await Query(client, question);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task Post_Query_Without_A_Question_Field_Returns_400()
    {
        await using var factory = CreateFactory(new InMemoryVectorStore(), new FakeLlmClient());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { includeChunks = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Query_With_An_Unavailable_Llm_Returns_503()
    {
        var llm = new FakeLlmClient { FailWith = "LlmUnavailable" };
        await using var factory = CreateFactory(new InMemoryVectorStore(), llm);
        using var client = factory.CreateClient();

        await Ingest(client);

        var response = await client.PostAsJsonAsync("/api/query", new { question = Question });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("LlmUnavailable", (await response.Content.ReadFromJsonAsync<QueryErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_Query_With_A_Timed_Out_Generation_Returns_504()
    {
        var llm = new FakeLlmClient { FailWith = "LlmTimeout" };
        await using var factory = CreateFactory(new InMemoryVectorStore(), llm);
        using var client = factory.CreateClient();

        await Ingest(client);

        var response = await client.PostAsJsonAsync("/api/query", new { question = Question });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("LlmTimeout", (await response.Content.ReadFromJsonAsync<QueryErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_Query_With_A_Failing_Vector_Store_Returns_503_Without_Generating()
    {
        var llm = new FakeLlmClient();
        await using var factory = CreateFactory(new InMemoryVectorStore { FailSearch = true }, llm);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { question = Question });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("VectorSearchFailed", (await response.Content.ReadFromJsonAsync<QueryErrorBody>())!.Error);
        Assert.Equal(0, llm.Calls);
    }

    private sealed record QueryBody(
        string? Answer,
        bool FoundAnswer,
        string Model,
        long DurationMs,
        IReadOnlyList<CitationBody> Citations);

    private sealed record CitationBody(
        string Title,
        string SourceId,
        string SourceType,
        int ChunkIndex,
        float Score,
        string? Text);

    private sealed record QueryErrorBody(string Error);
}
