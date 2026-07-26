using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using Microsoft.Extensions.Logging;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>Plain text files. Registered as keyed service <c>"txt"</c>.</summary>
public sealed class TextDocumentSource : FileDocumentSource
{
    public const string SourceKey = "txt";

    public TextDocumentSource(ILogger<TextDocumentSource> logger) : base(logger)
    {
    }

    public override SourceType SourceType => SourceType.Text;

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".txt"];

    protected override string ExtractionErrorCode => "TextExtractionFailed";

    protected override async Task<Result<string>> ExtractTextAsync(string path, CancellationToken ct) =>
        Result<string>.Success(await ReadAllTextAsync(path, ct));
}
