using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace AiKnowledgeAssistant.IntegrationTests;

/// <summary>
/// Integration tests over the scaffolded API surface (SPEC 01). The infrastructure
/// resources (Postgres/Qdrant/Ollama) are not required: no endpoint consumes them yet.
/// </summary>
public class ApiEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiEndpointsTests(WebApplicationFactory<Program> factory)
    {
        // Development is required for MapDefaultEndpoints to expose /health and /alive.
        _factory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
    }

    [Fact]
    public async Task Get_Health_Returns_200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_Query_Returns_501_NotImplemented()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/query",
            new { question = "¿Qué es RAG?" });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }
}
