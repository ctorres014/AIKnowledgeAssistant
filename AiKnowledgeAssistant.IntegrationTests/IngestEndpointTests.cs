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
/// End-to-end tests of <c>POST /api/ingest</c> and <c>GET /api/ingest/stats</c> over the real
/// controller, handler, locator, extractors and chunker. Only the two outbound dependencies —
/// Ollama and Qdrant — are in-memory doubles, so the suite needs no container runtime.
/// </summary>
public class IngestEndpointTests
{
    private static string Samples(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Samples", .. parts]);

    /// <summary>An app whose vector store and embedding provider are the supplied doubles.</summary>
    private static WebApplicationFactory<Program> CreateFactory(
        InMemoryVectorStore vectorStore,
        IEmbeddingGenerator? embeddings = null,
        Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:qdrant", "Endpoint=http://localhost:6334");
            builder.UseSetting("Aspire:Qdrant:Client:DisableHealthChecks", "true");

            configure?.Invoke(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IVectorStore>();
                services.AddSingleton<IVectorStore>(vectorStore);

                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton(embeddings ?? new FakeEmbeddingGenerator());
            });
        });

    private static async Task<(HttpStatusCode Status, IngestSummaryBody? Body)> Ingest(
        HttpClient client, string path, string? sourceType = null)
    {
        var response = await client.PostAsJsonAsync("/api/ingest", new { path, sourceType });

        return response.StatusCode == HttpStatusCode.OK
            ? (response.StatusCode, await response.Content.ReadFromJsonAsync<IngestSummaryBody>())
            : (response.StatusCode, null);
    }

    [Fact]
    public async Task Post_Ingest_A_Folder_With_Two_Markdown_Files_Returns_The_Expected_Summary()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var (status, body) = await Ingest(client, Samples("Markdown"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(body);
        Assert.Equal("Completed", body.Status);
        Assert.Equal(2, body.TotalFiles);
        Assert.Equal(2, body.Ingested);
        Assert.Equal(0, body.Skipped);
        Assert.Equal(0, body.FailedCount);
        Assert.Equal(0, body.Remaining);
        Assert.True(body.ChunksIndexed > 0, "expected chunks to be indexed");
        Assert.Empty(body.Failed);

        Assert.Equal(body.ChunksIndexed, store.VectorsCount);
    }

    [Fact]
    public async Task Post_Ingest_Twice_Over_Unchanged_Files_Skips_Everything_On_The_Second_Pass()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var (_, first) = await Ingest(client, Samples("Markdown"));
        var vectorsAfterFirst = store.VectorsCount;

        var (status, second) = await Ingest(client, Samples("Markdown"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, first!.Ingested);
        Assert.Equal(2, second!.Skipped);
        Assert.Equal(0, second.Ingested);
        Assert.Equal(0, second.ChunksIndexed);
        Assert.Equal(vectorsAfterFirst, store.VectorsCount);
    }

    [Fact]
    public async Task Post_Ingest_A_Mixed_Folder_Reads_Every_Supported_Format()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var (status, body) = await Ingest(client, Samples("Docs"));

        Assert.Equal(HttpStatusCode.OK, status);

        // handbook.txt, policies.md, guide.markdown, manual.pdf
        Assert.Equal(4, body!.TotalFiles);
        Assert.Equal(4, body.Ingested);
        Assert.Equal(0, body.FailedCount);
        Assert.Equal(4, store.SourceIds.Count);
    }

    [Fact]
    public async Task Post_Ingest_A_Single_File_Ingests_Only_That_File()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var (status, body) = await Ingest(client, Samples("Docs", "handbook.txt"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body!.TotalFiles);
        Assert.Equal(1, body.Ingested);
        Assert.Single(store.SourceIds);
    }

    [Fact]
    public async Task Post_Ingest_With_A_SourceType_Filters_To_That_Format()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var (status, body) = await Ingest(client, Samples("Docs"), sourceType: "markdown");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body!.TotalFiles);   // policies.md + guide.markdown
        Assert.Equal(2, body.Ingested);
    }

    [Fact]
    public async Task Post_Ingest_Reports_Unreadable_Files_Without_Failing_The_Batch()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        // Edge/ holds a corrupt PDF, an empty file, two readable text files and an unsupported .docx.
        var (status, body) = await Ingest(client, Samples("Edge"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(4, body!.TotalFiles);   // the .docx is not enumerated at all
        Assert.Equal(2, body.Ingested);      // bom-utf8.txt + utf16.txt
        Assert.Equal(2, body.FailedCount);   // corrupt.pdf + empty.txt

        Assert.Contains(body.Failed, f => f.Reason.StartsWith("PdfExtractionFailed", StringComparison.Ordinal));
        Assert.Contains(body.Failed, f => f.Reason.StartsWith("EmptyExtraction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_Ingest_Over_The_File_Cap_Returns_TooManyFiles_And_Indexes_Nothing()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(
            store, configure: builder => builder.UseSetting("Ingestion:MaxFilesPerRequest", "1"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ingest", new { path = Samples("Markdown") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TooManyFilesBody>();
        Assert.Equal("TooManyFiles", body!.Error);
        Assert.Equal(1, body.MaxFilesPerRequest);

        Assert.Equal(0, store.VectorsCount);
    }

    [Fact]
    public async Task Post_Ingest_Exceeding_The_Time_Budget_Returns_PartiallyCompleted()
    {
        var store = new InMemoryVectorStore();
        var slow = new FakeEmbeddingGenerator(delayPerCall: TimeSpan.FromMilliseconds(600));

        await using var factory = CreateFactory(
            store, slow, builder => builder.UseSetting("Ingestion:TimeoutSeconds", "1"));
        using var client = factory.CreateClient();

        var (status, body) = await Ingest(client, Samples("Docs"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("PartiallyCompleted", body!.Status);
        Assert.True(body.Ingested > 0, "expected some documents to be ingested");
        Assert.True(body.Remaining > 0, "expected some documents to be left over");
        Assert.Equal(body.TotalFiles, body.Ingested + body.Skipped + body.FailedCount + body.Remaining);

        // Nothing half-written: every stored document has all of its chunks, numbered from zero.
        foreach (var sourceId in store.SourceIds)
        {
            var chunks = store.ChunksOf(sourceId);
            Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
        }
    }

    [Fact]
    public async Task Post_Ingest_Retried_After_A_Partial_Run_Completes()
    {
        // One store shared by both apps: the second request sees what the first one stored.
        var store = new InMemoryVectorStore();

        int partiallyIngested;
        var slow = new FakeEmbeddingGenerator(delayPerCall: TimeSpan.FromMilliseconds(600));

        await using (var slowFactory = CreateFactory(
            store, slow, builder => builder.UseSetting("Ingestion:TimeoutSeconds", "1")))
        {
            using var slowClient = slowFactory.CreateClient();
            var (_, partial) = await Ingest(slowClient, Samples("Docs"));

            Assert.Equal("PartiallyCompleted", partial!.Status);
            partiallyIngested = partial.Ingested;
        }

        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var (status, retry) = await Ingest(client, Samples("Docs"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Completed", retry!.Status);
        Assert.Equal(0, retry.Remaining);
        Assert.Equal(partiallyIngested, retry.Skipped);
        Assert.Equal(4, retry.Ingested + retry.Skipped);
    }

    [Fact]
    public async Task Post_Ingest_A_Changed_File_Replaces_Its_Chunks()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var folder = Directory.CreateTempSubdirectory("ingest-endpoint");
        try
        {
            var file = Path.Combine(folder.FullName, "policy.md");

            // Long enough to produce several chunks.
            await File.WriteAllTextAsync(file, string.Join("\n\n", Enumerable.Range(0, 40)
                .Select(i => $"Paragraph {i} of the original policy, long enough to need more than one window.")));

            var (_, first) = await Ingest(client, folder.FullName);
            Assert.True(first!.ChunksIndexed > 1);

            await File.WriteAllTextAsync(file, "# Policy\n\nRewritten and much shorter.");

            var (status, second) = await Ingest(client, folder.FullName);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(1, second!.Ingested);
            Assert.Equal(0, second.Skipped);

            // The count reflects the new document, not the sum of both passes.
            Assert.Equal(second.ChunksIndexed, store.VectorsCount);
            Assert.True(store.VectorsCount < first.ChunksIndexed);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Get_Stats_Returns_The_Collection_And_Dimension()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ingest/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stats = await response.Content.ReadFromJsonAsync<StatsBody>();
        Assert.Equal("knowledge", stats!.Collection);
        Assert.Equal(768, stats.Dimension);
        Assert.Equal(0, stats.VectorsCount);
    }

    [Fact]
    public async Task Get_Stats_Reflects_What_Was_Ingested()
    {
        var store = new InMemoryVectorStore();
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var (_, body) = await Ingest(client, Samples("Markdown"));

        var stats = await client.GetFromJsonAsync<StatsBody>("/api/ingest/stats");

        Assert.Equal(body!.ChunksIndexed, stats!.VectorsCount);
    }

    private sealed record IngestSummaryBody(
        string Status,
        int TotalFiles,
        int Ingested,
        int Skipped,
        int FailedCount,
        int Remaining,
        int ChunksIndexed,
        long DurationMs,
        IReadOnlyList<IngestFailureBody> Failed);

    private sealed record IngestFailureBody(string Path, string Reason);

    private sealed record TooManyFilesBody(string Error, int MaxFilesPerRequest);

    private sealed record StatsBody(string Collection, long VectorsCount, int Dimension);
}
