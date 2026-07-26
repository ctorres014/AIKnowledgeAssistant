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
    /// <remarks>
    /// ServiceDefaults applies <c>AddStandardResilienceHandler</c> to every client, whose defaults
    /// (10s per attempt, 30s in total) are far too tight for CPU embedding: a batch that simply takes
    /// 15s gets cut, retried three times and reported as <c>EmbeddingRequestFailed</c>. This client
    /// drops that default handler and installs one sized for the workload.
    /// </remarks>
    private static void AddEmbeddings(IHostApplicationBuilder builder)
    {
        var attemptTimeout = TimeSpan.FromSeconds(Math.Max(
            1,
            builder.Configuration.GetValue(
                $"{EmbeddingOptions.SectionName}:{nameof(EmbeddingOptions.RequestTimeoutSeconds)}",
                new EmbeddingOptions().RequestTimeoutSeconds)));

        // EXTEXP0001: RemoveAllResilienceHandlers below is experimental. It is the only way to opt a
        // single client out of the ServiceDefaults default, and the alternative — stacking a second
        // handler — would compound both timeouts.
#pragma warning disable EXTEXP0001
        builder.Services
            .AddHttpClient<OllamaEmbeddingGenerator>(client =>
            {
                client.BaseAddress = OllamaEndpoint.Resolve(builder.Configuration);

                // Outermost bound; the resilience pipeline below cuts in well before this.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = attemptTimeout;

                // One retry only: a slow batch is slow again on the second try, and the value here is
                // covering the window where Ollama is still loading the model.
                options.Retry.MaxRetryAttempts = 1;
                options.TotalRequestTimeout.Timeout = attemptTimeout * 2;

                // Validation requires at least twice the attempt timeout.
                options.CircuitBreaker.SamplingDuration = attemptTimeout * 2;
            });
#pragma warning restore EXTEXP0001

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
