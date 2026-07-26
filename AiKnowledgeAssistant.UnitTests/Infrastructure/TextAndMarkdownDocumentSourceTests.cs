using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

public class TextAndMarkdownDocumentSourceTests
{
    private static TextDocumentSource Text() => new(NullLogger<TextDocumentSource>.Instance);

    private static MarkdownDocumentSource Markdown() => new(NullLogger<MarkdownDocumentSource>.Instance);

    [Fact]
    public void TextDocumentSource_DeclaresTextTypeAndExtension()
    {
        var source = Text();

        Assert.Equal(SourceType.Text, source.SourceType);
        Assert.Equal([".txt"], source.SupportedExtensions);
    }

    [Fact]
    public void MarkdownDocumentSource_DeclaresMarkdownTypeAndBothExtensions()
    {
        var source = Markdown();

        Assert.Equal(SourceType.Markdown, source.SourceType);
        Assert.Equal([".md", ".markdown"], source.SupportedExtensions);
    }

    [Fact]
    public async Task TextDocumentSource_ExtractsContentAndDerivesTitleFromFileName()
    {
        var result = await Text().ExtractAsync(SampleFiles.InDocs("handbook.txt"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var document = result.Value!;

        Assert.Equal("handbook", document.Title);
        Assert.Equal(SourceType.Text, document.SourceType);
        Assert.StartsWith("Employee Handbook", document.Content, StringComparison.Ordinal);
        Assert.Contains("core window from 10:00 to 16:00", document.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownDocumentSource_KeepsMarkdownSyntaxVerbatim()
    {
        var result = await Markdown().ExtractAsync(SampleFiles.InDocs("policies.md"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var document = result.Value!;

        Assert.Equal("policies", document.Title);
        Assert.Equal(SourceType.Markdown, document.SourceType);
        Assert.StartsWith("# Security Policies", document.Content, StringComparison.Ordinal);
        Assert.Contains("## Passwords", document.Content, StringComparison.Ordinal);
        Assert.Contains("- Laptops are encrypted at rest.", document.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownDocumentSource_HandlesTheMarkdownExtension()
    {
        var result = await Markdown().ExtractAsync(SampleFiles.InDocs("guide.markdown"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("guide", result.Value!.Title);
        Assert.Contains("Deployments run every Tuesday", result.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_HashesTheExtractedContent()
    {
        var result = await Text().ExtractAsync(SampleFiles.InDocs("handbook.txt"), CancellationToken.None);

        Assert.Equal(ContentHasher.Hash(result.Value!.Content), result.Value!.ContentHash);
    }

    [Fact]
    public async Task ExtractAsync_NormalizesTheSourceIdToAnAbsolutePath()
    {
        var path = SampleFiles.InDocs("handbook.txt");

        var result = await Text().ExtractAsync(path, CancellationToken.None);

        var sourceId = result.Value!.SourceId;
        Assert.DoesNotContain('\\', sourceId);
        Assert.EndsWith("samples/docs/handbook.txt", sourceId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SourcePath.ToSourceId(path), sourceId);
    }

    [Fact]
    public async Task ExtractAsync_StripsTheUtf8ByteOrderMark()
    {
        var result = await Text().ExtractAsync(SampleFiles.InEdge("bom-utf8.txt"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.StartsWith("Política de vacaciones", result.Value!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('﻿', result.Value!.Content);
    }

    [Fact]
    public async Task ExtractAsync_DetectsUtf16FromTheByteOrderMark()
    {
        var result = await Text().ExtractAsync(SampleFiles.InEdge("utf16.txt"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Contains("23 días laborables", result.Value!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_EmptyFile_ReturnsEmptyExtractionFailure()
    {
        var result = await Text().ExtractAsync(SampleFiles.InEdge("empty.txt"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmptyExtraction", result.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsync_MissingFile_ReturnsFailureInsteadOfThrowing()
    {
        var result = await Text().ExtractAsync(
            SampleFiles.InDocs("does-not-exist.txt"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("FileNotFound", result.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsync_BlankLocator_ReturnsFailure()
    {
        var result = await Text().ExtractAsync("  ", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PathMissing", result.ErrorCode);
    }
}
