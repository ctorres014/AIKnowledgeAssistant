using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Rag;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AiKnowledgeAssistant.Infrastructure.DependencyInjection;

/// <summary>Composition root of the query path: RAG pipeline and Knowledge Orchestrator.</summary>
public static class RagRegistration
{
    /// <summary>
    /// Registers typed configuration, the Ollama chat client (keyed <c>"ollama"</c> and as the
    /// default <see cref="ILlmClient"/>), the grounded prompt builder, the RAG pipeline and the
    /// orchestrator that fronts them.
    /// </summary>
    /// <remarks>
    /// Retrieval reuses what <c>AddIngestion</c> already registered — the same
    /// <see cref="IEmbeddingGenerator"/> and <see cref="IVectorStore"/> — so questions and chunks are
    /// embedded into the same vector space. Call this after <c>AddIngestion</c>.
    /// </remarks>
    public static IHostApplicationBuilder AddRag(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));

        AddLlmClient(builder);

        builder.Services.AddSingleton<GroundedPromptBuilder>();
        builder.Services.AddScoped<RagPipeline>();
        builder.Services.AddScoped<KnowledgeOrchestrator>();

        AddTelemetry(builder.Services);

        return builder;
    }

    /// <summary>
    /// Surfaces the query path's spans and counters through the Aspire dashboard. ServiceDefaults sets
    /// up the providers and exporters; this only opts our own source and meter in, exactly as
    /// <c>AddIngestion</c> does for ingestion.
    /// </summary>
    private static void AddTelemetry(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider(
            tracing => tracing.AddSource(RagTelemetry.ActivitySourceName));

        services.ConfigureOpenTelemetryMeterProvider(
            metrics => metrics.AddMeter(RagTelemetry.MeterName));
    }

    /// <summary>
    /// Ollama as a typed <see cref="HttpClient"/>, exposed both keyed (<c>"ollama"</c>) and as the
    /// default provider. OpenAI would slot in here as a second key without touching callers.
    /// </summary>
    /// <remarks>
    /// The standard resilience handler ServiceDefaults applies to every client is removed rather than
    /// retuned: <see cref="OllamaLlmClient"/> owns the time budget through
    /// <see cref="RagOptions.TimeoutSeconds"/>, and leaving the handler in place would cut generation
    /// at its own 10s attempt timeout — surfacing a slow answer as <c>LlmUnavailable</c> instead of
    /// the <c>LlmTimeout</c> the contract promises. Retrying is wrong here for the same reason: a
    /// 60s generation is just as slow the second time, and each attempt costs the user another minute.
    /// </remarks>
    private static void AddLlmClient(IHostApplicationBuilder builder)
    {
        // EXTEXP0001: RemoveAllResilienceHandlers is experimental, and the only way to opt a single
        // client out of the ServiceDefaults default without stacking a second handler on top.
#pragma warning disable EXTEXP0001
        builder.Services
            .AddHttpClient<OllamaLlmClient>(client =>
            {
                client.BaseAddress = OllamaEndpoint.Resolve(
                    builder.Configuration, OllamaEndpoint.ChatModelResourceName);

                // The client's own linked CancellationTokenSource is the only deadline that applies.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.Services.AddTransient<ILlmClient>(sp => sp.GetRequiredService<OllamaLlmClient>());
        builder.Services.AddKeyedTransient<ILlmClient>(
            OllamaLlmClient.ProviderKey, (sp, _) => sp.GetRequiredService<OllamaLlmClient>());
    }
}
