using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Infrastructure.Rag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AiKnowledgeAssistant.IntegrationTests;

/// <summary>
/// The query path's DI graph, resolved from the real container. Nothing here dials out: building an
/// <see cref="ILlmClient"/> only creates its <see cref="HttpClient"/>.
/// </summary>
public class RagCompositionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RagCompositionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:qdrant", "Endpoint=http://localhost:6334");
            builder.UseSetting("Aspire:Qdrant:Client:DisableHealthChecks", "true");
        });
    }

    private IServiceScope CreateScope() => _factory.Services.CreateScope();

    [Fact]
    public void KnowledgeOrchestrator_ResolvesWithItsWholeDependencyGraph()
    {
        using var scope = CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<KnowledgeOrchestrator>());
    }

    [Fact]
    public void RagPipelineAndPromptBuilder_Resolve()
    {
        using var scope = CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RagPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GroundedPromptBuilder>());
    }

    [Fact]
    public void LlmClient_ResolvesAsTheDefaultAndUnderTheOllamaKey()
    {
        using var scope = CreateScope();

        var @default = scope.ServiceProvider.GetRequiredService<ILlmClient>();
        var keyed = scope.ServiceProvider.GetRequiredKeyedService<ILlmClient>(OllamaLlmClient.ProviderKey);

        Assert.IsType<OllamaLlmClient>(@default);
        Assert.IsType<OllamaLlmClient>(keyed);
    }

    [Fact]
    public void LlmClient_ReportsTheConfiguredModel()
    {
        using var scope = CreateScope();

        Assert.Equal("llama3.2:3b", scope.ServiceProvider.GetRequiredService<ILlmClient>().Model);
    }

    [Fact]
    public void RagOptions_BindFromConfiguration()
    {
        using var scope = CreateScope();

        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>().Value;

        Assert.Equal("llama3.2:3b", options.Model);
        Assert.Equal(5, options.TopK);
        Assert.Equal(0.5f, options.MinScore);
        Assert.Equal(60, options.TimeoutSeconds);
        Assert.Equal(500, options.MaxAnswerTokens);
        Assert.Equal(0.2f, options.Temperature);
    }
}
