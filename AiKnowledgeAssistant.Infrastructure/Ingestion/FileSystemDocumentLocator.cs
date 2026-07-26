using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Resolves a local path — one file, or a folder walked recursively — into the documents to ingest.
/// Files whose extension no registered source claims are ignored rather than reported as failures.
/// </summary>
public sealed class FileSystemDocumentLocator : IDocumentLocator
{
    private readonly IReadOnlyList<IDocumentSource> _sources;
    private readonly Dictionary<string, IDocumentSource> _byExtension;

    public FileSystemDocumentLocator(IEnumerable<IDocumentSource> sources)
    {
        _sources = [.. sources];
        _byExtension = new Dictionary<string, IDocumentSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
        {
            foreach (var extension in source.SupportedExtensions)
            {
                _byExtension[extension] = source;
            }
        }
    }

    public Result<IReadOnlyList<LocatedDocument>> Locate(string path, string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<IReadOnlyList<LocatedDocument>>.Failure(
                "PathNotFound: no path was supplied.", "PathNotFound");
        }

        IDocumentSource? forced = null;
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            forced = ResolveSource(sourceType);
            if (forced is null)
            {
                return Result<IReadOnlyList<LocatedDocument>>.Failure(
                    $"UnknownSourceType: '{sourceType}' is not a supported source type.", "UnknownSourceType");
            }
        }

        var candidates = EnumerateCandidates(path);
        if (candidates is null)
        {
            return Result<IReadOnlyList<LocatedDocument>>.Failure(
                $"PathNotFound: {path}", "PathNotFound");
        }

        var located = new List<LocatedDocument>();
        foreach (var file in candidates.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var source = Resolve(file, forced);
            if (source is not null)
            {
                located.Add(new LocatedDocument(file, source));
            }
        }

        if (located.Count == 0)
        {
            return Result<IReadOnlyList<LocatedDocument>>.Failure(
                $"NoSupportedFiles: {path} contains no file this pipeline can read.", "NoSupportedFiles");
        }

        return Result<IReadOnlyList<LocatedDocument>>.Success(located);
    }

    /// <summary>The files under <paramref name="path"/>, or <c>null</c> when it does not exist.</summary>
    private static IEnumerable<string>? EnumerateCandidates(string path)
    {
        if (File.Exists(path))
        {
            return [path];
        }

        if (Directory.Exists(path))
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        }

        return null;
    }

    /// <summary>
    /// The source for a file: the forced one when it claims the extension, otherwise the source
    /// registered for that extension. <c>null</c> means "skip this file".
    /// </summary>
    private IDocumentSource? Resolve(string file, IDocumentSource? forced)
    {
        var extension = Path.GetExtension(file);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        if (forced is not null)
        {
            return forced.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? forced
                : null;
        }

        return _byExtension.GetValueOrDefault(extension);
    }

    /// <summary>Maps the request's <c>sourceType</c> onto a registered source, accepting the <c>"txt"</c> alias.</summary>
    private IDocumentSource? ResolveSource(string sourceType)
    {
        var normalized = sourceType.Trim();

        if (string.Equals(normalized, "txt", StringComparison.OrdinalIgnoreCase))
        {
            normalized = nameof(SourceType.Text);
        }
        else if (string.Equals(normalized, "md", StringComparison.OrdinalIgnoreCase))
        {
            normalized = nameof(SourceType.Markdown);
        }

        return Enum.TryParse<SourceType>(normalized, ignoreCase: true, out var parsed)
            ? _sources.FirstOrDefault(s => s.SourceType == parsed)
            : null;
    }
}
