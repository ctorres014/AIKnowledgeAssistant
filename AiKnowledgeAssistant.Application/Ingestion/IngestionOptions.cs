namespace AiKnowledgeAssistant.Application.Ingestion;

/// <summary>Configuration section <c>Ingestion</c>: the bounds that keep a synchronous ingest safe.</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Files accepted per request. A larger batch is rejected outright with <c>TooManyFiles</c>.</summary>
    public int MaxFilesPerRequest { get; init; } = 100;

    /// <summary>
    /// Budget for one ingest call. On expiry the run stops between documents and returns a partial
    /// summary. Keep this below the request timeout of Kestrel and any proxy in front of it
    /// (typically 100–120s) so the client receives the summary instead of a dead connection.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 90;
}
