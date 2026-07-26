namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>Configuration section <c>Chunking</c>. Token counts are estimated (~4 chars ≈ 1 token).</summary>
public sealed class ChunkingOptions
{
    public const string SectionName = "Chunking";

    /// <summary>Upper bound of estimated tokens per chunk.</summary>
    public int MaxTokens { get; init; } = 500;

    /// <summary>Estimated tokens repeated from the tail of the previous chunk.</summary>
    public int OverlapTokens { get; init; } = 50;
}
