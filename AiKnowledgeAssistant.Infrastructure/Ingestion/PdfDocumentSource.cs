using System.Text;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// PDF files, read with PdfPig (Apache-2.0, pure managed). Text is extracted page by page and
/// joined with a blank line so the chunker can prefer page boundaries when it cuts.
/// Registered as keyed service <c>"pdf"</c>.
/// </summary>
/// <remarks>
/// Scanned, image-only PDFs extract to nothing and are reported as <c>EmptyExtraction</c> rather
/// than indexed as useless chunks — OCR is out of scope for this spec.
/// </remarks>
public sealed class PdfDocumentSource : FileDocumentSource
{
    public const string SourceKey = "pdf";

    public PdfDocumentSource(ILogger<PdfDocumentSource> logger) : base(logger)
    {
    }

    public override SourceType SourceType => SourceType.Pdf;

    public override IReadOnlyCollection<string> SupportedExtensions { get; } = [".pdf"];

    protected override string ExtractionErrorCode => "PdfExtractionFailed";

    protected override Task<Result<string>> ExtractTextAsync(string path, CancellationToken ct) =>
        // PdfPig is synchronous and CPU bound; keep it off the request thread.
        Task.Run(() => ExtractText(path, ct), ct);

    private static Result<string> ExtractText(string path, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(path);

        var builder = new StringBuilder();

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var text = ContentOrderTextExtractor.GetText(page);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(text.Trim());
        }

        return Result<string>.Success(builder.ToString());
    }
}
