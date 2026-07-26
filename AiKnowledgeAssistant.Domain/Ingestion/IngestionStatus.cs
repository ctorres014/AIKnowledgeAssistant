namespace AiKnowledgeAssistant.Domain.Ingestion;

/// <summary>
/// Outcome of an ingestion batch. <see cref="PartiallyCompleted"/> means the run was cut
/// between documents (timeout) and files remain unprocessed; retrying the same request resumes.
/// </summary>
public enum IngestionStatus
{
    Completed,
    PartiallyCompleted
}
