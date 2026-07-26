using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AiKnowledgeAssistant.Infrastructure.DependencyInjection;

/// <summary>Composition root of the ingestion pipeline.</summary>
public static class IngestionRegistration
{
    /// <summary>
    /// Registers typed configuration, the document sources (keyed by <c>sourceType</c>), the chunker,
    /// the Ollama embedding client and the Qdrant vector store, plus the ingestion use case.
    /// </summary>
    public static IHostApplicationBuilder AddIngestion(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddOptions(builder);
        AddDocumentSources(builder.Services);
        AddEmbeddings(builder);
        AddVectorStore(builder);

        builder.Services.AddSingleton<ITextChunker, FixedWindowTextChunker>();
        builder.Services.AddScoped<IngestDocumentsHandler>();

        AddTelemetry(builder.Services);

        return builder;
    }

    /// <summary>
    /// Surfaces the pipeline's spans and counters through the Aspire dashboard. ServiceDefaults sets
    /// up the providers and exporters; this only opts our own source and meter in.
    /// </summary>
    private static void AddTelemetry(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider(
            tracing => tracing.AddSource(IngestionTelemetry.ActivitySourceName));

        services.ConfigureOpenTelemetryMeterProvider(
            metrics => metrics.AddMeter(IngestionTelemetry.MeterName));
    }

    private static void AddOptions(IHostApplicationBuilder builder)
    {
        builder.Services
            .Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.SectionName));
        builder.Services
            .Configure<ChunkingOptions>(builder.Configuration.GetSection(ChunkingOptions.SectionName));
        builder.Services
            .Configure<VectorStoreOptions>(builder.Configuration.GetSection(VectorStoreOptions.SectionName));
        builder.Services
            .Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
    }

    /// <summary>
    /// Each source is registered three ways: as its concrete type (one instance), keyed by its
    /// <c>sourceType</c> discriminator, and in the <see cref="IDocumentSource"/> set the locator walks.
    /// Keyed registrations are deliberately not part of that set, so both are needed.
    /// </summary>
    private static void AddDocumentSources(IServiceCollection services)
    {
        services.AddSingleton<PdfDocumentSource>();
        services.AddSingleton<TextDocumentSource>();
        services.AddSingleton<MarkdownDocumentSource>();

        services.AddKeyedSingleton<IDocumentSource>(
            PdfDocumentSource.SourceKey, (sp, _) => sp.GetRequiredService<PdfDocumentSource>());
        services.AddKeyedSingleton<IDocumentSource>(
            TextDocumentSource.SourceKey, (sp, _) => sp.GetRequiredService<TextDocumentSource>());
        services.AddKeyedSingleton<IDocumentSource>(
            MarkdownDocumentSource.SourceKey, (sp, _) => sp.GetRequiredService<MarkdownDocumentSource>());

        services.AddSingleton<IDocumentSource>(sp => sp.GetRequiredService<PdfDocumentSource>());
        services.AddSingleton<IDocumentSource>(sp => sp.GetRequiredService<TextDocumentSource>());
        services.AddSingleton<IDocumentSource>(sp => sp.GetRequiredService<MarkdownDocumentSource>());

        services.AddSingleton<IDocumentLocator, FileSystemDocumentLocator>();
    }

    /// <summary>
    /// Ollama as a typed <see cref="HttpClient"/>, exposed both keyed (<c>"ollama"</c>) and as the
    /// default provider. OpenAI would slot in here as a second key without touching callers.
    /// </summary>
    private static void AddEmbeddings(IHostApplicationBuilder builder)
    {
        builder.Services
            .AddHttpClient<OllamaEmbeddingGenerator>(client =>
            {
                client.BaseAddress = OllamaEndpoint.Resolve(builder.Configuration);

                // Embedding a batch on CPU can be slow; the ingest deadline is the real bound.
                client.Timeout = TimeSpan.FromMinutes(5);
            });

        builder.Services.AddTransient<IEmbeddingGenerator>(
            sp => sp.GetRequiredService<OllamaEmbeddingGenerator>());
        builder.Services.AddKeyedTransient<IEmbeddingGenerator>(
            OllamaEmbeddingGenerator.ProviderKey, (sp, _) => sp.GetRequiredService<OllamaEmbeddingGenerator>());
    }

    private static void AddVectorStore(IHostApplicationBuilder builder)
    {
        builder.AddQdrantClient(QdrantResourceName);

        builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();

        // Creates the collection on boot; refuses to start on a dimension mismatch.
        builder.Services.AddHostedService<VectorStoreInitializer>();
    }

    /// <summary>Connection name of the Qdrant resource in the app host.</summary>
    private const string QdrantResourceName = "qdrant";
}
