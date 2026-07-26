using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using Microsoft.Extensions.Logging;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Markdown files. The text is kept verbatim — no rendering and no syntax stripping — so headings
/// and lists survive into the grounding context. Registered as keyed service <c>"markdown"</c>.
/// </summary>
public sealed class MarkdownDocumentSource : FileDocumentSource
{
    public const string SourceKey = "markdown";

    public MarkdownDocumentSource(ILogger<MarkdownDocumentSource> logger) : base(logger)
    {
    }

    public override SourceType SourceType => SourceType.Markdown;

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".md", ".markdown"];

    protected override string ExtractionErrorCode => "MarkdownExtractionFailed";

    protected override async Task<Result<string>> ExtractTextAsync(string path, CancellationToken ct) =>
        Result<string>.Success(await ReadAllTextAsync(path, ct));
}
