using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.UnitTests.Fakes;

/// <summary>In-memory document source: returns whatever the test registered for each path.</summary>
public sealed class FakeDocumentSource : IDocumentSource
{
    private readonly Dictionary<string, Result<RawDocument>> _documents = new(StringComparer.OrdinalIgnoreCase);

    public SourceType SourceType => SourceType.Markdown;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".md"];

    public int ExtractCalls { get; private set; }

    /// <summary>Registers a readable document at <paramref name="path"/>.</summary>
    public FakeDocumentSource WithDocument(string path, string content, string? title = null)
    {
        _documents[path] = Result<RawDocument>.Success(new RawDocument(
            path,
            SourceType.Markdown,
            title ?? Path.GetFileNameWithoutExtension(path),
            content,
            ContentHasher.Hash(content)));

        return this;
    }

    /// <summary>Registers a path whose extraction fails.</summary>
    public FakeDocumentSource WithFailure(string path, string reason = "PdfExtractionFailed: broken file")
    {
        _documents[path] = Result<RawDocument>.Failure(reason, reason.Split(':')[0]);
        return this;
    }

    public Task<Result<RawDocument>> ExtractAsync(string locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ExtractCalls++;

        return Task.FromResult(_documents.TryGetValue(locator, out var document)
            ? document
            : Result<RawDocument>.Failure($"FileNotFound: {locator}", "FileNotFound"));
    }
}
