namespace AiKnowledgeAssistant.Application.Rag;

/// <summary>Configuration section <c>Rag</c>.</summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>
    /// Ollama model used to generate answers. Development on CPU uses <c>llama3.2:3b</c>
    /// (~2 GB, 10–20s per query); <c>qwen2.5:7b</c> is the target model for a GPU deployment.
    /// Switching between them is a configuration edit, never a code change.
    /// </summary>
    public string Model { get; init; } = "llama3.2:3b";

    /// <summary>Maximum chunks the vector search may return.</summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Minimum similarity a chunk needs to enter the grounding context. Qdrant returns K results
    /// regardless of relevance; without a threshold the model receives noise and starts filling
    /// gaps from its own knowledge, which is exactly what FR-003 forbids.
    /// </summary>
    public float MinScore { get; init; } = 0.5f;

    /// <summary>Budget for a single generation request, after which the query fails as <c>LlmTimeout</c>.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Upper bound on generated tokens.</summary>
    public int MaxAnswerTokens { get; init; } = 500;

    /// <summary>Low on purpose: FR-003 asks for grounded answers, not creative ones.</summary>
    public float Temperature { get; init; } = 0.2f;
}
