using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Ingestion;
using AiKnowledgeAssistant.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Embeddings from a local Ollama instance via <c>POST /api/embed</c>. Registered as keyed service
/// <c>"ollama"</c> and as the default <see cref="IEmbeddingGenerator"/>.
/// </summary>
/// <remarks>
/// Texts are sent in batches of <see cref="EmbeddingOptions.BatchSize"/> instead of one request per
/// chunk. A provider outage is an expected outcome, not an exception: it comes back as
/// <c>Result.Failure</c> so the caller can mark just the affected documents as failed.
/// </remarks>
public sealed class OllamaEmbeddingGenerator : IEmbeddingGenerator
{
    /// <summary>Keyed-service name under which this provider is registered.</summary>
    public const string ProviderKey = "ollama";

    private const string EmbedPath = "api/embed";

    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OllamaEmbeddingGenerator> _logger;

    public OllamaEmbeddingGenerator(
        HttpClient http,
        IOptions<EmbeddingOptions> options,
        ILogger<OllamaEmbeddingGenerator> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public int Dimension => _options.Dimension;

    public async Task<Result<IReadOnlyList<float[]>>> GenerateAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return Result<IReadOnlyList<float[]>>.Success([]);
        }

        var batchSize = Math.Max(1, _options.BatchSize);
        var vectors = new List<float[]>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = texts.Skip(offset).Take(batchSize).ToArray();

            var batchResult = await EmbedBatchAsync(batch, ct);
            if (!batchResult.IsSuccess)
            {
                return Result<IReadOnlyList<float[]>>.Failure(batchResult.Error!, batchResult.ErrorCode);
            }

            vectors.AddRange(batchResult.Value!);
        }

        return Result<IReadOnlyList<float[]>>.Success(vectors);
    }

    private async Task<Result<float[][]>> EmbedBatchAsync(string[] batch, CancellationToken ct)
    {
        EmbedResponse? payload;

        try
        {
            using var response = await _http.PostAsJsonAsync(
                EmbedPath, new EmbedRequest(_options.Model, batch), ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Ollama embeddings returned {StatusCode} for model {Model}",
                    (int)response.StatusCode, _options.Model);

                return Result<float[][]>.Failure(
                    $"EmbeddingRequestFailed: Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    "EmbeddingRequestFailed");
            }

            payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Connection refused, DNS failure, client timeout, malformed body.
            _logger.LogWarning(ex, "Ollama embeddings call failed for model {Model}", _options.Model);

            return Result<float[][]>.Failure(
                $"EmbeddingRequestFailed: {ex.Message}", "EmbeddingRequestFailed");
        }

        return Validate(payload, batch.Length);
    }

    private Result<float[][]> Validate(EmbedResponse? payload, int expectedCount)
    {
        var embeddings = payload?.Embeddings;

        if (embeddings is null)
        {
            return Result<float[][]>.Failure(
                "EmbeddingResponseInvalid: the response carried no embeddings.", "EmbeddingResponseInvalid");
        }

        if (embeddings.Length != expectedCount)
        {
            return Result<float[][]>.Failure(
                $"EmbeddingResponseInvalid: expected {expectedCount} vectors, received {embeddings.Length}.",
                "EmbeddingResponseInvalid");
        }

        foreach (var vector in embeddings)
        {
            if (vector is null || vector.Length != _options.Dimension)
            {
                return Result<float[][]>.Failure(
                    $"EmbeddingDimensionMismatch: expected {_options.Dimension} values, received {vector?.Length ?? 0}.",
                    "EmbeddingDimensionMismatch");
            }
        }

        return Result<float[][]>.Success(embeddings);
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] float[][]? Embeddings);
}
