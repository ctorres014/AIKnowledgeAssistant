using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.Application.Abstractions;

/// <summary>
/// A pluggable document origin. Implementations are registered as keyed services under the
/// lowercase source discriminator (<c>"pdf"</c>, <c>"txt"</c>, <c>"markdown"</c>) so the
/// pipeline can resolve one by <c>sourceType</c> or by file extension.
/// </summary>
public interface IDocumentSource
{
    /// <summary>Origin this source produces.</summary>
    SourceType SourceType { get; }

    /// <summary>Extensions handled by this source, lowercase and dot-prefixed (e.g. <c>[".md", ".markdown"]</c>).</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    /// Extracts plain text from a single locator (a file path today, a URI for future remote sources).
    /// Expected failures — unreadable file, corrupt document, empty extraction — come back as
    /// <c>Result.Failure</c>, never as an exception.
    /// </summary>
    Task<Result<RawDocument>> ExtractAsync(string locator, CancellationToken ct);
}
