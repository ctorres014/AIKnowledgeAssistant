using System.Net;
using System.Net.Http.Json;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.IntegrationTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.IntegrationTests;

/// <summary>
/// Integration tests over the API surface. No container runtime is required: the Qdrant
/// connection is stubbed and its health check disabled, so nothing dials out.
/// </summary>
public class ApiEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryVectorStore _vectorStore = new();

    public ApiEndpointsTests(WebApplicationFactory<Program> factory)
    {
        // Development is required for MapDefaultEndpoints to expose /health and /alive.
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            // The Qdrant client is constructed lazily, but Aspire also registers a health check
            // that would dial the container. Stub the endpoint and turn that probe off.
            builder.UseSetting("ConnectionStrings:qdrant", "Endpoint=http://localhost:6334");
            builder.UseSetting("Aspire:Qdrant:Client:DisableHealthChecks", "true");

            // Swap the two outbound dependencies for in-memory doubles: the pipeline, the controller
            // and the startup initializer are all the real thing, but nothing leaves the process.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IVectorStore>();
                services.AddSingleton<IVectorStore>(_vectorStore);

                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator>(new FakeEmbeddingGenerator());
            });
        });
    }

    [Fact]
    public async Task Get_Health_Returns_200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The endpoint is real since SPEC 03; a blank question is rejected before any dependency is
    /// touched, which is why it is safe to assert here, where no LLM double is registered. The full
    /// query surface is covered by the dedicated query endpoint tests.
    /// </summary>
    [Fact]
    public async Task Post_Query_With_A_Blank_Question_Returns_400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query", new { question = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Ingest_With_A_Missing_Path_Returns_400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/ingest",
            new { path = @"C:\does-not-exist\anywhere" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PathNotFound", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_Ingest_With_An_Unknown_SourceType_Returns_400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/ingest",
            new { path = AppContext.BaseDirectory, sourceType = "docx" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("UnknownSourceType", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_Ingest_With_A_Blank_Path_Returns_400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ingest", new { path = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The composition root wires the whole ingestion pipeline: if any dependency were missing or had
    /// a mismatched lifetime, resolving the handler would throw here.
    /// </summary>
    [Fact]
    public void Ingestion_Pipeline_Resolves_From_The_Container()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.NotNull(provider.GetRequiredService<IngestDocumentsHandler>());
        Assert.NotNull(provider.GetRequiredService<ITextChunker>());
        Assert.NotNull(provider.GetRequiredService<IEmbeddingGenerator>());
        Assert.NotNull(provider.GetRequiredService<IVectorStore>());
        Assert.NotNull(provider.GetRequiredService<IDocumentLocator>());
    }

    [Theory]
    [InlineData("pdf", SourceType.Pdf)]
    [InlineData("txt", SourceType.Text)]
    [InlineData("markdown", SourceType.Markdown)]
    public void Document_Sources_Resolve_By_Their_Keys(string key, SourceType expected)
    {
        using var scope = _factory.Services.CreateScope();

        var source = scope.ServiceProvider.GetRequiredKeyedService<IDocumentSource>(key);

        Assert.Equal(expected, source.SourceType);
    }

    [Fact]
    public void Embedding_Generator_Resolves_By_The_Ollama_Key_And_As_Default()
    {
        using var scope = _factory.Services.CreateScope();

        var keyed = scope.ServiceProvider.GetRequiredKeyedService<IEmbeddingGenerator>("ollama");
        var @default = scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator>();

        Assert.Equal(768, keyed.Dimension);
        Assert.Equal(768, @default.Dimension);
    }

    [Fact]
    public void Typed_Options_Bind_From_Configuration()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.Equal("nomic-embed-text", provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Model);
        Assert.Equal(500, provider.GetRequiredService<IOptions<ChunkingOptions>>().Value.MaxTokens);
        Assert.Equal("knowledge", provider.GetRequiredService<IOptions<VectorStoreOptions>>().Value.CollectionName);
        Assert.Equal(100, provider.GetRequiredService<IOptions<IngestionOptions>>().Value.MaxFilesPerRequest);
    }
}
