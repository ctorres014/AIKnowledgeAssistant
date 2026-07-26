using System.Net;
using System.Text;
using System.Text.Json;
using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

public class OllamaEmbeddingGeneratorTests
{
    private const int Dimension = 768;

    private readonly List<HttpRequestMessage> _requests = [];

    /// <summary>Mocked transport returning <paramref name="responses"/> in order, recording every request.</summary>
    private OllamaEmbeddingGenerator CreateGenerator(
        IEnumerable<HttpResponseMessage> responses,
        int batchSize = 16,
        int dimension = Dimension)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken ct) =>
            {
                // Buffer the body before the caller disposes the request.
                _ = await request.Content!.ReadAsStringAsync(ct);
                _requests.Add(request);
                return queue.Dequeue();
            });

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("http://ollama:11434/") };

        return new OllamaEmbeddingGenerator(
            client,
            Options.Create(new EmbeddingOptions { Dimension = dimension, BatchSize = batchSize }),
            NullLogger<OllamaEmbeddingGenerator>.Instance);
    }

    private static HttpResponseMessage Ok(int vectorCount, int dimension = Dimension)
    {
        var embeddings = Enumerable
            .Range(0, vectorCount)
            .Select(i => Enumerable.Range(0, dimension).Select(j => (i + j) * 0.001f).ToArray())
            .ToArray();

        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { embeddings }));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string[] Texts(int count) =>
        [.. Enumerable.Range(0, count).Select(i => $"chunk number {i}")];

    [Fact]
    public async Task GenerateAsync_ReturnsOneVectorPerTextWithTheConfiguredDimension()
    {
        var generator = CreateGenerator([Ok(3)]);

        var result = await generator.GenerateAsync(Texts(3), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(3, result.Value!.Count);
        Assert.All(result.Value!, v => Assert.Equal(Dimension, v.Length));
    }

    [Fact]
    public void Dimension_ComesFromOptions()
    {
        Assert.Equal(Dimension, CreateGenerator([]).Dimension);
    }

    [Fact]
    public async Task GenerateAsync_PostsToTheEmbedEndpointWithModelAndInputs()
    {
        var generator = CreateGenerator([Ok(2)]);

        await generator.GenerateAsync(Texts(2), CancellationToken.None);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://ollama:11434/api/embed", request.RequestUri!.ToString());

        var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
        using var json = JsonDocument.Parse(body);

        Assert.Equal("nomic-embed-text", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task GenerateAsync_SplitsTextsIntoBatchesOfTheConfiguredSize()
    {
        // 5 texts, batch size 2 => 2 + 2 + 1
        var generator = CreateGenerator([Ok(2), Ok(2), Ok(1)], batchSize: 2);

        var result = await generator.GenerateAsync(Texts(5), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(5, result.Value!.Count);
        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public async Task GenerateAsync_PreservesInputOrderAcrossBatches()
    {
        var generator = CreateGenerator([Ok(1), Ok(1)], batchSize: 1);

        var result = await generator.GenerateAsync(Texts(2), CancellationToken.None);

        // Ok(n) seeds vector i with (i + j) * 0.001; both batches start at i = 0.
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(0f, result.Value![0][0]);
        Assert.Equal(0f, result.Value![1][0]);
    }

    [Fact]
    public async Task GenerateAsync_EmptyInput_ReturnsEmptyAndCallsNothing()
    {
        var generator = CreateGenerator([]);

        var result = await generator.GenerateAsync([], CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task GenerateAsync_HttpError_ReturnsFailure()
    {
        var generator = CreateGenerator([Json(HttpStatusCode.InternalServerError, "{}")]);

        var result = await generator.GenerateAsync(Texts(2), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingRequestFailed", result.ErrorCode);
        Assert.Contains("500", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_HttpErrorOnALaterBatch_FailsTheWholeCall()
    {
        var generator = CreateGenerator(
            [Ok(1), Json(HttpStatusCode.ServiceUnavailable, "{}")], batchSize: 1);

        var result = await generator.GenerateAsync(Texts(2), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingRequestFailed", result.ErrorCode);
    }

    [Fact]
    public async Task GenerateAsync_TransportFailure_ReturnsFailureInsteadOfThrowing()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var generator = new OllamaEmbeddingGenerator(
            new HttpClient(handler.Object) { BaseAddress = new Uri("http://ollama:11434/") },
            Options.Create(new EmbeddingOptions()),
            NullLogger<OllamaEmbeddingGenerator>.Instance);

        var result = await generator.GenerateAsync(Texts(1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingRequestFailed", result.ErrorCode);
        Assert.Contains("connection refused", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_WrongVectorCount_ReturnsFailure()
    {
        var generator = CreateGenerator([Ok(1)]);

        var result = await generator.GenerateAsync(Texts(3), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingResponseInvalid", result.ErrorCode);
    }

    [Fact]
    public async Task GenerateAsync_MissingEmbeddingsField_ReturnsFailure()
    {
        var generator = CreateGenerator([Json(HttpStatusCode.OK, """{"model":"nomic-embed-text"}""")]);

        var result = await generator.GenerateAsync(Texts(1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingResponseInvalid", result.ErrorCode);
    }

    [Fact]
    public async Task GenerateAsync_WrongDimension_ReturnsFailure()
    {
        // The server answers with 1024-wide vectors while the collection expects 768.
        var generator = CreateGenerator([Ok(1, dimension: 1024)], dimension: Dimension);

        var result = await generator.GenerateAsync(Texts(1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmbeddingDimensionMismatch", result.ErrorCode);
    }

    [Fact]
    public async Task GenerateAsync_CancelledToken_PropagatesCancellation()
    {
        var generator = CreateGenerator([Ok(1)]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => generator.GenerateAsync(Texts(1), cts.Token));
    }
}
