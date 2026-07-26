using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

public class FileSystemDocumentLocatorTests
{
    private static FileSystemDocumentLocator CreateLocator() => new(
    [
        new PdfDocumentSource(NullLogger<PdfDocumentSource>.Instance),
        new TextDocumentSource(NullLogger<TextDocumentSource>.Instance),
        new MarkdownDocumentSource(NullLogger<MarkdownDocumentSource>.Instance)
    ]);

    private static IReadOnlyList<LocatedDocument> Locate(string path, string? sourceType = null)
    {
        var result = CreateLocator().Locate(path, sourceType);

        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    [Fact]
    public void Locate_Folder_WalksRecursivelyAndIgnoresUnsupportedExtensions()
    {
        var located = Locate(SampleFiles.Root);

        var names = located.Select(d => Path.GetFileName(d.Path)).ToArray();

        Assert.Contains("handbook.txt", names);
        Assert.Contains("policies.md", names);
        Assert.Contains("guide.markdown", names);
        Assert.Contains("manual.pdf", names);
        Assert.Contains("corrupt.pdf", names);      // supported extension: reading it fails later, not here
        Assert.DoesNotContain("unsupported.docx", names);
        Assert.All(located, d =>
            Assert.Contains(Path.GetExtension(d.Path), new[] { ".pdf", ".txt", ".md", ".markdown" },
                StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Locate_Folder_PairsEachFileWithTheSourceForItsExtension()
    {
        var located = Locate(SampleFiles.Docs);

        Assert.Equal(SourceType.Text, Single(located, "handbook.txt").Source.SourceType);
        Assert.Equal(SourceType.Markdown, Single(located, "policies.md").Source.SourceType);
        Assert.Equal(SourceType.Markdown, Single(located, "guide.markdown").Source.SourceType);
        Assert.Equal(SourceType.Pdf, Single(located, "manual.pdf").Source.SourceType);
    }

    [Fact]
    public void Locate_Folder_ReturnsAStableOrder()
    {
        var first = Locate(SampleFiles.Root).Select(d => d.Path);
        var second = Locate(SampleFiles.Root).Select(d => d.Path);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Locate_SingleFile_ReturnsThatFileOnly()
    {
        var located = Locate(SampleFiles.InDocs("handbook.txt"));

        var document = Assert.Single(located);
        Assert.Equal("handbook.txt", Path.GetFileName(document.Path));
        Assert.Equal(SourceType.Text, document.Source.SourceType);
    }

    [Fact]
    public void Locate_MarkdownOnlyFolder_ReturnsExactlyTwoDocuments()
    {
        var located = Locate(SampleFiles.MarkdownOnly);

        Assert.Equal(2, located.Count);
        Assert.All(located, d => Assert.Equal(SourceType.Markdown, d.Source.SourceType));
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("Markdown")]
    [InlineData("md")]
    public void Locate_WithForcedMarkdown_KeepsOnlyMarkdownFiles(string sourceType)
    {
        var located = Locate(SampleFiles.Docs, sourceType);

        Assert.Equal(2, located.Count);
        Assert.All(located, d => Assert.Equal(SourceType.Markdown, d.Source.SourceType));
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("Text")]
    public void Locate_WithForcedText_KeepsOnlyTextFiles(string sourceType)
    {
        var located = Locate(SampleFiles.Docs, sourceType);

        var document = Assert.Single(located);
        Assert.Equal("handbook.txt", Path.GetFileName(document.Path));
    }

    [Fact]
    public void Locate_WithForcedPdf_KeepsOnlyPdfFiles()
    {
        var located = Locate(SampleFiles.Docs, "pdf");

        var document = Assert.Single(located);
        Assert.Equal("manual.pdf", Path.GetFileName(document.Path));
    }

    [Fact]
    public void Locate_MissingPath_FailsWithPathNotFound()
    {
        var result = CreateLocator().Locate(Path.Combine(SampleFiles.Root, "no-such-folder"), null);

        Assert.False(result.IsSuccess);
        Assert.Equal("PathNotFound", result.ErrorCode);
    }

    [Fact]
    public void Locate_BlankPath_FailsWithPathNotFound()
    {
        var result = CreateLocator().Locate("   ", null);

        Assert.False(result.IsSuccess);
        Assert.Equal("PathNotFound", result.ErrorCode);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("confluence")]  // valid enum value, but no source is registered for it
    [InlineData("")]
    public void Locate_UnknownSourceType_FailsWithUnknownSourceType(string sourceType)
    {
        var result = CreateLocator().Locate(SampleFiles.Docs, sourceType);

        if (sourceType.Length == 0)
        {
            // An omitted sourceType is legal: it means "infer from the extension".
            Assert.True(result.IsSuccess);
            return;
        }

        Assert.False(result.IsSuccess);
        Assert.Equal("UnknownSourceType", result.ErrorCode);
    }

    [Fact]
    public void Locate_FolderWithoutSupportedFiles_FailsWithNoSupportedFiles()
    {
        var empty = Directory.CreateTempSubdirectory("locator-tests");
        try
        {
            File.WriteAllText(Path.Combine(empty.FullName, "readme.docx"), "ignored");

            var result = CreateLocator().Locate(empty.FullName, null);

            Assert.False(result.IsSuccess);
            Assert.Equal("NoSupportedFiles", result.ErrorCode);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Locate_SingleFileWithUnsupportedExtension_FailsWithNoSupportedFiles()
    {
        var result = CreateLocator().Locate(SampleFiles.InEdge("unsupported.docx"), null);

        Assert.False(result.IsSuccess);
        Assert.Equal("NoSupportedFiles", result.ErrorCode);
    }

    [Fact]
    public void Locate_SingleFileNotMatchingTheForcedSource_FailsWithNoSupportedFiles()
    {
        var result = CreateLocator().Locate(SampleFiles.InDocs("handbook.txt"), "markdown");

        Assert.False(result.IsSuccess);
        Assert.Equal("NoSupportedFiles", result.ErrorCode);
    }

    private static LocatedDocument Single(IReadOnlyList<LocatedDocument> located, string fileName) =>
        located.Single(d => string.Equals(Path.GetFileName(d.Path), fileName, StringComparison.OrdinalIgnoreCase));
}
