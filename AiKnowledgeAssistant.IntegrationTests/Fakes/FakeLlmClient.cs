using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.IntegrationTests.Fakes;

/// <summary>
/// Deterministic answer generator standing in for Ollama. It records every prompt it received, which
/// is what turns "no context, no LLM call" from an intention into an assertable fact at the HTTP
/// boundary.
/// </summary>
/// <remarks>
/// The failure paths are modelled as the result codes the real client returns — <c>LlmUnavailable</c>
/// for a provider that is down or still pulling the model, <c>LlmTimeout</c> for a generation past
/// its budget. The timing itself belongs to <c>OllamaLlmClient</c>'s own unit tests; what the
/// endpoint owns, and what is asserted here, is the mapping of those codes to 503 and 504.
/// </remarks>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly string _answer;
    private readonly Lock _gate = new();
    private readonly List<LlmPrompt> _prompts = [];

    public FakeLlmClient(string answer = "El onboarding dura cinco jornadas.", string model = "llama3.2:3b")
    {
        _answer = answer;
        Model = model;
    }

    public string Model { get; }

    /// <summary>Failure code to return instead of an answer, e.g. <c>LlmUnavailable</c> or <c>LlmTimeout</c>.</summary>
    public string? FailWith { get; init; }

    public IReadOnlyList<LlmPrompt> Prompts
    {
        get
        {
            lock (_gate)
            {
                return [.. _prompts];
            }
        }
    }

    public int Calls => Prompts.Count;

    public Task<Result<LlmCompletion>> CompleteAsync(LlmPrompt prompt, CancellationToken ct)
    {
        lock (_gate)
        {
            _prompts.Add(prompt);
        }

        if (FailWith is not null)
        {
            return Task.FromResult(Result<LlmCompletion>.Failure($"{FailWith}: simulated.", FailWith));
        }

        return Task.FromResult(Result<LlmCompletion>.Success(new LlmCompletion(_answer, 412, 37)));
    }
}
