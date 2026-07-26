namespace AiKnowledgeAssistant.Domain.Ingestion;

/// <summary>
/// A document after text extraction and before chunking.
/// </summary>
/// <param name="SourceId">Stable identity of the source (normalized absolute path).</param>
/// <param name="SourceType">Origin format of the document.</param>
/// <param name="Title">File name without extension.</param>
/// <param name="Content">Plain text already extracted from the source.</param>
/// <param name="ContentHash">Lowercase hex SHA-256 of <paramref name="Content"/>.</param>
public sealed record RawDocument(
    string SourceId,
    SourceType SourceType,
    string Title,
    string Content,
    string ContentHash);
