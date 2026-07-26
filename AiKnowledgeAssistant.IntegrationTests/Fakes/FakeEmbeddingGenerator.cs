using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.IntegrationTests.Fakes;

/// <summary>
/// Deterministic embeddings without Ollama. An optional per-call delay makes the ingest time budget
/// observable through the HTTP endpoint.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly TimeSpan _delayPerCall;

    public FakeEmbeddingGenerator(int dimension = 768, TimeSpan? delayPerCall = null)
    {
        Dimension = dimension;
        _delayPerCall = delayPerCall ?? TimeSpan.Zero;
    }

    public int Dimension { get; }

    public async Task<Result<IReadOnlyList<float[]>>> GenerateAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (_delayPerCall > TimeSpan.Zero)
        {
            await Task.Delay(_delayPerCall, ct);
        }

        var vectors = texts
            .Select(t => Enumerable.Range(0, Dimension).Select(i => (t.Length + i) * 0.001f).ToArray())
            .ToArray();

        return Result<IReadOnlyList<float[]>>.Success(vectors);
    }
}
