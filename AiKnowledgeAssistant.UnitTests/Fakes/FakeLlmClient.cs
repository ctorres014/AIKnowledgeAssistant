using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.UnitTests.Fakes;

/// <summary>
/// Deterministic answer generator. Records every prompt it received, so "the LLM was never called"
/// is an assertable fact rather than an intention.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly string _answer;

    public FakeLlmClient(string answer = "El onboarding dura cinco jornadas.", string model = "llama3.2:3b")
    {
        _answer = answer;
        Model = model;
    }

    public string Model { get; }

    public List<LlmPrompt> Prompts { get; } = [];

    public int Calls => Prompts.Count;

    /// <summary>Failure code to return instead of an answer, e.g. <c>LlmUnavailable</c> or <c>LlmTimeout</c>.</summary>
    public string? FailWith { get; init; }

    public Task<Result<LlmCompletion>> CompleteAsync(LlmPrompt prompt, CancellationToken ct)
    {
        Prompts.Add(prompt);

        if (FailWith is not null)
        {
            return Task.FromResult(Result<LlmCompletion>.Failure($"{FailWith}: simulated.", FailWith));
        }

        return Task.FromResult(Result<LlmCompletion>.Success(new LlmCompletion(_answer, 412, 37)));
    }
}
