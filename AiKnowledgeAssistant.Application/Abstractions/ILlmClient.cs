using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.Application.Abstractions;

/// <summary>
/// Generates an answer from a grounded prompt. Pluggable by provider (Ollama today, OpenAI later)
/// via keyed services, the same mechanism used by <see cref="IEmbeddingGenerator"/>.
/// </summary>
public interface ILlmClient
{
    /// <summary>Configured model, propagated to the response and to the traces.</summary>
    string Model { get; }

    /// <summary>
    /// Completes a prompt. Provider outages and timeouts are expected failures and come back as
    /// <c>Result.Failure</c> with codes <c>LlmUnavailable</c> and <c>LlmTimeout</c>.
    /// </summary>
    Task<Result<LlmCompletion>> CompleteAsync(LlmPrompt prompt, CancellationToken ct);
}

/// <summary>A grounded prompt split into its system and user blocks.</summary>
/// <param name="System">Instructions that constrain the model to the supplied context.</param>
/// <param name="User">Retrieved context plus the question.</param>
public sealed record LlmPrompt(string System, string User);

/// <summary>What the provider produced.</summary>
/// <param name="Text">Generated answer.</param>
/// <param name="PromptTokens">Tokens consumed by the prompt, when the provider reports them.</param>
/// <param name="CompletionTokens">Tokens generated, when the provider reports them.</param>
public sealed record LlmCompletion(string Text, int? PromptTokens, int? CompletionTokens);
