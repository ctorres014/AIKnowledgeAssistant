namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>Configuration section <c>VectorStore</c>.</summary>
public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    /// <summary>Qdrant collection holding the knowledge chunks.</summary>
    public string CollectionName { get; init; } = "knowledge";

    /// <summary>Distance metric of the collection (<c>Cosine</c>, <c>Dot</c>, <c>Euclid</c>).</summary>
    public string Distance { get; init; } = "Cosine";
}
