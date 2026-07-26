namespace AiKnowledgeAssistant.Domain.Ingestion;

/// <summary>
/// Origin of an ingested document. Doubles as the discriminator used to resolve
/// the matching document source via keyed DI.
/// </summary>
public enum SourceType
{
    Pdf,
    Text,
    Markdown,
    Confluence
}
