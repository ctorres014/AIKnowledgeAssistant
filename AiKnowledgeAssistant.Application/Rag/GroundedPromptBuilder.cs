using System.Text;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Rag;

namespace AiKnowledgeAssistant.Application.Rag;

/// <summary>
/// Turns retrieved chunks and a question into the grounded prompt. Its own type, and not a private
/// helper of the pipeline, because FR-003 lives or dies here: the wording is the only thing keeping
/// the model from answering out of its own knowledge, so it has to be assertable on its own.
/// </summary>
public sealed class GroundedPromptBuilder
{
    /// <summary>
    /// The three rules FR-003 rests on: answer only from the context, say so when the context falls
    /// short, and reply in the language of the question.
    /// </summary>
    public const string SystemPrompt =
        "Eres un asistente de conocimiento corporativo. Responde ÚNICAMENTE con la información " +
        "del CONTEXTO. Si el contexto no contiene la respuesta, dilo explícitamente y no uses " +
        "conocimiento propio. Responde en el mismo idioma en el que está formulada la pregunta.";

    /// <summary>
    /// Builds the prompt for a question that has context. Callers must not invoke this with an empty
    /// <paramref name="chunks"/>: with nothing retrieved the pipeline answers
    /// <c>FoundAnswer: false</c> without ever reaching the model.
    /// </summary>
    public LlmPrompt Build(string question, IReadOnlyList<RetrievedChunk> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            throw new ArgumentException(
                "A grounded prompt needs at least one chunk; with none the pipeline answers without the LLM.",
                nameof(chunks));
        }

        var user = new StringBuilder("CONTEXTO:").AppendLine().AppendLine();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            // The bracketed number is the position in the context, not the chunk's index in its
            // document — both are shown because a citation needs the latter.
            user
                .Append('[').Append(i + 1).Append("] ")
                .Append(chunk.Title)
                .Append(" (fragmento ").Append(chunk.ChunkIndex).Append(')')
                .AppendLine()
                .AppendLine(chunk.Text)
                .AppendLine();
        }

        user.Append("PREGUNTA: ").Append(question.Trim());

        return new LlmPrompt(SystemPrompt, user.ToString());
    }
}
