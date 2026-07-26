namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>
/// Request to ingest a local path.
/// </summary>
/// <param name="Path">A file, or a folder walked recursively.</param>
/// <param name="SourceType">
/// Optional discriminator (<c>pdf</c>, <c>txt</c>, <c>markdown</c>). When omitted the source is
/// inferred per file from its extension.
/// </param>
public sealed record IngestDocumentsCommand(string Path, string? SourceType);
