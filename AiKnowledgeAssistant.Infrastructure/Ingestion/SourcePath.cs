namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Normalization of local file paths into the stable <c>SourceId</c> stored in the vector payload.
/// </summary>
/// <remarks>
/// A source ID is the absolute path with <c>/</c> separators, lowercased on case-insensitive
/// file systems. This couples the index to the ingesting machine — an accepted MVP limitation
/// recorded in the spec; ingest from one canonical path.
/// </remarks>
public static class SourcePath
{
    /// <summary>Absolute, separator-normalized, case-normalized identity of a local file.</summary>
    public static string ToSourceId(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path).Replace('\\', '/');

        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }
}
