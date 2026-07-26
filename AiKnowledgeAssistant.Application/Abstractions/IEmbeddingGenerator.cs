using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.Application.Abstractions;

/// <summary>
/// Turns text into vectors. Pluggable by provider (Ollama today, OpenAI later) via keyed services.
/// </summary>
public interface IEmbeddingGenerator
{
    /// <summary>Dimension of every vector produced, and of the Qdrant collection.</summary>
    int Dimension { get; }

    /// <summary>
    /// Embeds a batch of texts, returning one vector per input in the same order.
    /// A provider outage is an expected failure and comes back as <c>Result.Failure</c>.
    /// </summary>
    Task<Result<IReadOnlyList<float[]>>> GenerateAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
