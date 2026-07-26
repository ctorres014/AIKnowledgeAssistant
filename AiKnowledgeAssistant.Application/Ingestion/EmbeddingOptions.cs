namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>Configuration section <c>Embeddings</c>.</summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>Ollama model used to embed chunks.</summary>
    public string Model { get; init; } = "nomic-embed-text";

    /// <summary>Vector dimension produced by <see cref="Model"/>; must match the Qdrant collection.</summary>
    public int Dimension { get; init; } = 768;

    /// <summary>Chunks sent per embedding request, to avoid one HTTP call per chunk.</summary>
    public int BatchSize { get; init; } = 16;

    /// <summary>
    /// Budget for a single embedding request. Embedding a batch on CPU takes tens of seconds, well
    /// past the 10s the standard resilience handler allows by default, so this raises the per-attempt
    /// timeout of the Ollama client. Keep it above the slowest observed batch.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 120;
}
