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
}
