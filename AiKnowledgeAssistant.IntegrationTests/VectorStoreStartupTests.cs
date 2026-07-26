using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.IntegrationTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiKnowledgeAssistant.IntegrationTests;

/// <summary>
/// Covers the startup contract of the collection initializer: it runs on boot, it is safe to run
/// again, and an incompatible collection stops the app instead of being indexed into.
/// </summary>
public class VectorStoreStartupTests
{
    private static WebApplicationFactory<Program> CreateFactory(IVectorStore vectorStore) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:qdrant", "Endpoint=http://localhost:6334");
            builder.UseSetting("Aspire:Qdrant:Client:DisableHealthChecks", "true");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IVectorStore>();
                services.AddSingleton(vectorStore);

                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator>(new FakeEmbeddingGenerator());
            });
        });

    [Fact]
    public async Task Startup_Prepares_The_Collection()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);

        // Creating a client boots the host, which runs the hosted services.
        using var client = factory.CreateClient();
        await client.GetAsync("/health");

        Assert.Equal(1, store.EnsureCollectionCalls);
    }

    [Fact]
    public async Task Starting_Twice_Against_An_Existing_Collection_Does_Not_Fail()
    {
        // The same store instance survives both hosts, standing in for a collection that already exists.
        var store = new InMemoryVectorStore();

        for (var boot = 1; boot <= 2; boot++)
        {
            await using var factory = CreateFactory(store);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/health");

            Assert.True(response.IsSuccessStatusCode, $"boot {boot} failed: {response.StatusCode}");
        }

        Assert.Equal(2, store.EnsureCollectionCalls);
    }

    [Fact]
    public async Task An_Incompatible_Collection_Fails_Startup_Loudly()
    {
        // A store whose dimension does not match the configured model: the real QdrantVectorStore
        // throws InvalidOperationException here, and the initializer must not swallow it.
        await using var factory = CreateFactory(new MismatchedVectorStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/health");
        });

        Assert.Contains("dimension", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_Unreachable_Vector_Store_Does_Not_Fail_Startup()
    {
        // Transient unavailability is reported by health checks, not by refusing to boot.
        await using var factory = CreateFactory(new UnreachableVectorStore());

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    private sealed class MismatchedVectorStore : InMemoryVectorStoreStub
    {
        public override Task EnsureCollectionAsync(CancellationToken ct) =>
            throw new InvalidOperationException(
                "Qdrant collection 'knowledge' has dimension 1024 but the configured embedding model produces 768.");
    }

    private sealed class UnreachableVectorStore : InMemoryVectorStoreStub
    {
        public override Task EnsureCollectionAsync(CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    /// <summary>Minimal <see cref="IVectorStore"/> whose startup behaviour each test overrides.</summary>
    private abstract class InMemoryVectorStoreStub : IVectorStore
    {
        public abstract Task EnsureCollectionAsync(CancellationToken ct);

        public Task<bool> ExistsWithHashAsync(string sourceId, string contentHash, CancellationToken ct) =>
            Task.FromResult(false);

        public Task DeleteBySourceIdAsync(string sourceId, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(
            IReadOnlyList<(Domain.Ingestion.DocumentChunk Chunk, float[] Vector)> points, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<VectorStoreStats> GetStatsAsync(CancellationToken ct) =>
            Task.FromResult(new VectorStoreStats("knowledge", 0, 768));
    }
}
