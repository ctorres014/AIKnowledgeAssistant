namespace AiKnowledgeAssistant.Domain.Rag;

/// <summary>
/// Outcome of the RAG pipeline, before it is mapped to the HTTP DTO.
/// </summary>
/// <param name="Text">Generated answer; <c>null</c> when <paramref name="FoundAnswer"/> is false.</param>
/// <param name="FoundAnswer">False when no chunk cleared the score threshold, in which case the
/// LLM was never called.</param>
/// <param name="Citations">Chunks that grounded the answer; empty when
/// <paramref name="FoundAnswer"/> is false. The chunk text always travels here — whether it is
/// serialized is a presentation decision taken by the controller.</param>
/// <param name="Model">Configured generation model, propagated to the response and the traces
/// even on the no-results path.</param>
/// <param name="DurationMs">Wall-clock duration of the pipeline.</param>
public sealed record Answer(
    string? Text,
    bool FoundAnswer,
    IReadOnlyList<RetrievedChunk> Citations,
    string Model,
    long DurationMs);
