using AiKnowledgeAssistant.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Creates the vector collection at startup so the first ingest does not have to.
/// </summary>
/// <remarks>
/// A dimension mismatch on an existing collection is fatal: booting against a collection built for a
/// different embedding model would mix vector spaces and quietly ruin retrieval, so the app refuses
/// to start. An unreachable vector store is <em>not</em> fatal — that is a transient condition the
/// health checks already report, and failing startup would only turn a slow dependency into an
/// outage. Running twice is safe: an existing collection is verified, not recreated.
/// </remarks>
public sealed class VectorStoreInitializer : IHostedService
{
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<VectorStoreInitializer> _logger;

    public VectorStoreInitializer(IVectorStore vectorStore, ILogger<VectorStoreInitializer> logger)
    {
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _vectorStore.EnsureCollectionAsync(cancellationToken);
            _logger.LogInformation("Vector collection is ready");
        }
        catch (InvalidOperationException)
        {
            // Incompatible collection: fail loudly rather than index into the wrong vector space.
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not prepare the vector collection at startup. Ingestion will fail until the " +
                "vector store is reachable; the first successful call will create the collection.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
