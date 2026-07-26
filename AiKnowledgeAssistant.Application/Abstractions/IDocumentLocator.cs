using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.Application.Abstractions;

/// <summary>
/// Turns the requested input — a single file or a folder to walk — into the ordered list of
/// documents to ingest, each already paired with the source that can read it.
/// </summary>
public interface IDocumentLocator
{
    /// <summary>
    /// Resolves <paramref name="path"/> into locatable documents. When <paramref name="sourceType"/>
    /// is supplied only files that source handles are returned; otherwise the source is resolved
    /// per file by extension and unsupported extensions are ignored.
    /// </summary>
    /// <returns>
    /// Failure with code <c>PathNotFound</c>, <c>UnknownSourceType</c> or <c>NoSupportedFiles</c>
    /// for the three bad-request cases; otherwise the documents in a stable order.
    /// </returns>
    Result<IReadOnlyList<LocatedDocument>> Locate(string path, string? sourceType);
}

/// <summary>A file to ingest and the source that will extract its text.</summary>
public sealed record LocatedDocument(string Path, IDocumentSource Source);
