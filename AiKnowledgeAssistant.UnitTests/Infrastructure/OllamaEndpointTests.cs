using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Configuration;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

public class OllamaEndpointTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Resolve_PrefersTheEmbeddingModelResource()
    {
        var configuration = Configuration(
            ("ConnectionStrings:embedding", "Endpoint=http://ollama-model:11434;Model=nomic-embed-text"),
            ("ConnectionStrings:ollama", "Endpoint=http://ollama-server:11434"));

        Assert.Equal("http://ollama-model:11434/", OllamaEndpoint.Resolve(configuration).ToString());
    }

    [Fact]
    public void Resolve_FallsBackToTheServerResource()
    {
        var configuration = Configuration(("ConnectionStrings:ollama", "Endpoint=http://ollama-server:11434"));

        Assert.Equal("http://ollama-server:11434/", OllamaEndpoint.Resolve(configuration).ToString());
    }

    [Fact]
    public void Resolve_AcceptsABareUrlConnectionString()
    {
        var configuration = Configuration(("ConnectionStrings:embedding", "http://localhost:11434"));

        Assert.Equal("http://localhost:11434/", OllamaEndpoint.Resolve(configuration).ToString());
    }

    [Fact]
    public void Resolve_IgnoresAConnectionStringWithoutAUsableEndpoint()
    {
        var configuration = Configuration(
            ("ConnectionStrings:embedding", "Model=nomic-embed-text"),
            ("ConnectionStrings:ollama", "Endpoint=http://ollama-server:11434"));

        Assert.Equal("http://ollama-server:11434/", OllamaEndpoint.Resolve(configuration).ToString());
    }

    [Fact]
    public void Resolve_WithNothingConfigured_UsesTheServiceDiscoveryName()
    {
        Assert.Equal("http://ollama/", OllamaEndpoint.Resolve(Configuration()).ToString());
    }

    [Fact]
    public void Resolve_ForTheChatModel_PrefersTheChatResource()
    {
        var configuration = Configuration(
            ("ConnectionStrings:chat", "Endpoint=http://ollama-chat:11434;Model=llama3.2:3b"),
            ("ConnectionStrings:embedding", "Endpoint=http://ollama-embed:11434;Model=nomic-embed-text"),
            ("ConnectionStrings:ollama", "Endpoint=http://ollama-server:11434"));

        Assert.Equal(
            "http://ollama-chat:11434/",
            OllamaEndpoint.Resolve(configuration, OllamaEndpoint.ChatModelResourceName).ToString());
    }

    /// <summary>Both models are served by the same instance, so the server resource still answers.</summary>
    [Fact]
    public void Resolve_ForTheChatModel_FallsBackToTheServerResource()
    {
        var configuration = Configuration(
            ("ConnectionStrings:embedding", "Endpoint=http://ollama-embed:11434"),
            ("ConnectionStrings:ollama", "Endpoint=http://ollama-server:11434"));

        Assert.Equal(
            "http://ollama-server:11434/",
            OllamaEndpoint.Resolve(configuration, OllamaEndpoint.ChatModelResourceName).ToString());
    }

    [Fact]
    public void Resolve_ForEmbeddings_IgnoresTheChatResource()
    {
        var configuration = Configuration(
            ("ConnectionStrings:chat", "Endpoint=http://ollama-chat:11434"),
            ("ConnectionStrings:embedding", "Endpoint=http://ollama-embed:11434"));

        Assert.Equal("http://ollama-embed:11434/", OllamaEndpoint.Resolve(configuration).ToString());
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnTheEndpointKey()
    {
        var configuration = Configuration(("ConnectionStrings:embedding", "endpoint=http://localhost:11434;model=x"));

        Assert.Equal("http://localhost:11434/", OllamaEndpoint.Resolve(configuration).ToString());
    }
}
