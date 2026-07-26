using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

public class PdfDocumentSourceTests
{
    private static PdfDocumentSource Pdf() => new(NullLogger<PdfDocumentSource>.Instance);

    [Fact]
    public void DeclaresPdfTypeAndExtension()
    {
        var source = Pdf();

        Assert.Equal(SourceType.Pdf, source.SourceType);
        Assert.Equal([".pdf"], source.SupportedExtensions);
    }

    [Fact]
    public async Task ExtractAsync_ValidPdf_ReturnsNonEmptyText()
    {
        var result = await Pdf().ExtractAsync(SampleFiles.InDocs("manual.pdf"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.Content));
    }

    [Fact]
    public async Task ExtractAsync_ValidPdf_ExtractsTextFromEveryPage()
    {
        var result = await Pdf().ExtractAsync(SampleFiles.InDocs("manual.pdf"), CancellationToken.None);

        var content = result.Value!.Content;

        Assert.Contains("Operations Manual", content, StringComparison.Ordinal);
        Assert.Contains("Incident response", content, StringComparison.Ordinal);  // page 1
        Assert.Contains("Backups", content, StringComparison.Ordinal);            // page 2
        Assert.Contains("\n\n", content, StringComparison.Ordinal);               // pages joined
    }

    [Fact]
    public async Task ExtractAsync_ValidPdf_DerivesTitleAndHash()
    {
        var result = await Pdf().ExtractAsync(SampleFiles.InDocs("manual.pdf"), CancellationToken.None);

        var document = result.Value!;

        Assert.Equal("manual", document.Title);
        Assert.Equal(SourceType.Pdf, document.SourceType);
        Assert.Equal(ContentHasher.Hash(document.Content), document.ContentHash);
    }

    [Fact]
    public async Task ExtractAsync_CorruptPdf_ReturnsFailureAndDoesNotThrow()
    {
        var result = await Pdf().ExtractAsync(SampleFiles.InEdge("corrupt.pdf"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PdfExtractionFailed", result.ErrorCode);
        Assert.StartsWith("PdfExtractionFailed:", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ExtractAsync_MissingPdf_ReturnsFailure()
    {
        var result = await Pdf().ExtractAsync(SampleFiles.InDocs("nope.pdf"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("FileNotFound", result.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsync_IsDeterministic()
    {
        var first = await Pdf().ExtractAsync(SampleFiles.InDocs("manual.pdf"), CancellationToken.None);
        var second = await Pdf().ExtractAsync(SampleFiles.InDocs("manual.pdf"), CancellationToken.None);

        Assert.Equal(first.Value!.ContentHash, second.Value!.ContentHash);
    }
}
