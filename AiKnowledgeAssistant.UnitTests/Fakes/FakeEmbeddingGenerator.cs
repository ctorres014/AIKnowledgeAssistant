using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.UnitTests.Fakes;

/// <summary>
/// Deterministic embedding generator. Can be told to fail, or to take a fixed time per call so
/// timeout behaviour is testable without a real provider.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly TimeSpan _delayPerCall;
    private readonly string? _failureFor;

    public FakeEmbeddingGenerator(
        int dimension = 768,
        TimeSpan? delayPerCall = null,
        string? failureFor = null)
    {
        Dimension = dimension;
        _delayPerCall = delayPerCall ?? TimeSpan.Zero;
        _failureFor = failureFor;
    }

    public int Dimension { get; }

    public int Calls { get; private set; }

    public int TextsEmbedded { get; private set; }

    /// <summary>Set to fail every call, simulating a provider outage.</summary>
    public bool FailAlways { get; init; }

    public async Task<Result<IReadOnlyList<float[]>>> GenerateAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        Calls++;

        if (_delayPerCall > TimeSpan.Zero)
        {
            await Task.Delay(_delayPerCall, ct);
        }

        ct.ThrowIfCancellationRequested();

        if (FailAlways)
        {
            return Result<IReadOnlyList<float[]>>.Failure(
                "EmbeddingRequestFailed: provider unavailable.", "EmbeddingRequestFailed");
        }

        if (_failureFor is not null && texts.Any(t => t.Contains(_failureFor, StringComparison.Ordinal)))
        {
            return Result<IReadOnlyList<float[]>>.Failure(
                "EmbeddingRequestFailed: provider rejected this text.", "EmbeddingRequestFailed");
        }

        TextsEmbedded += texts.Count;

        var vectors = texts
            .Select(t => Enumerable.Range(0, Dimension).Select(i => (t.Length + i) * 0.001f).ToArray())
            .ToArray();

        return Result<IReadOnlyList<float[]>>.Success(vectors);
    }
}
