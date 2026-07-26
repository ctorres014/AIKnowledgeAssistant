using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;
using AiKnowledgeAssistant.Domain.Ingestion;
using Microsoft.Extensions.Logging;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Shared plumbing for document sources backed by a local file: existence checks, text extraction
/// dispatch, empty-content rejection and hashing. Every expected failure comes back as
/// <c>Result.Failure</c> so a single bad file cannot abort a batch.
/// </summary>
public abstract class FileDocumentSource : IDocumentSource
{
    protected FileDocumentSource(ILogger logger) => Logger = logger;

    protected ILogger Logger { get; }

    public abstract SourceType SourceType { get; }

    public abstract IReadOnlyCollection<string> SupportedExtensions { get; }

    public async Task<Result<RawDocument>> ExtractAsync(string locator, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return Result<RawDocument>.Failure("PathMissing: no file path was supplied.", "PathMissing");
        }

        if (!File.Exists(locator))
        {
            return Result<RawDocument>.Failure($"FileNotFound: {locator}", "FileNotFound");
        }

        Result<string> text;
        try
        {
            text = await ExtractTextAsync(locator, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Extraction failed for {Locator}", locator);
            return Result<RawDocument>.Failure($"{ExtractionErrorCode}: {ex.Message}", ExtractionErrorCode);
        }

        if (!text.IsSuccess)
        {
            return Result<RawDocument>.Failure(text.Error!, text.ErrorCode);
        }

        var content = text.Value!;
        if (string.IsNullOrWhiteSpace(content))
        {
            // Scanned PDFs and blank files land here: indexing them would pollute retrieval.
            return Result<RawDocument>.Failure($"EmptyExtraction: {locator} produced no text.", "EmptyExtraction");
        }

        return Result<RawDocument>.Success(new RawDocument(
            SourcePath.ToSourceId(locator),
            SourceType,
            Path.GetFileNameWithoutExtension(locator),
            content,
            ContentHasher.Hash(content)));
    }

    /// <summary>Error code reported when the underlying reader throws.</summary>
    protected abstract string ExtractionErrorCode { get; }

    /// <summary>Format-specific text extraction. The file is known to exist.</summary>
    protected abstract Task<Result<string>> ExtractTextAsync(string path, CancellationToken ct);

    /// <summary>
    /// Reads a text file, honouring a byte order mark when present and defaulting to UTF-8
    /// (without throwing on invalid bytes) when it is not.
    /// </summary>
    protected static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);

        using var reader = new StreamReader(
            stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return await reader.ReadToEndAsync(ct);
    }
}
