using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiKnowledgeAssistant.Infrastructure.Rag;

/// <summary>
/// Answer generation from a local Ollama instance via <c>POST /api/chat</c>. Registered as keyed
/// service <c>"ollama"</c> and as the default <see cref="ILlmClient"/>.
/// </summary>
/// <remarks>
/// The whole answer comes back in one JSON body (<c>stream: false</c>): SPEC 04's semantic cache
/// needs the complete text to store it, and the PRD does not ask for streaming.
/// A provider outage and a generation that outlasts <see cref="RagOptions.TimeoutSeconds"/> are both
/// expected outcomes, and come back as <c>Result.Failure</c> with <c>LlmUnavailable</c> and
/// <c>LlmTimeout</c> so the endpoint can answer 503 and 504 instead of an opaque 500.
/// </remarks>
public sealed class OllamaLlmClient : ILlmClient
{
    /// <summary>Keyed-service name under which this provider is registered.</summary>
    public const string ProviderKey = "ollama";

    private const string ChatPath = "api/chat";

    private readonly HttpClient _http;
    private readonly RagOptions _options;
    private readonly ILogger<OllamaLlmClient> _logger;

    public OllamaLlmClient(
        HttpClient http,
        IOptions<RagOptions> options,
        ILogger<OllamaLlmClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public string Model => _options.Model;

    public async Task<Result<LlmCompletion>> CompleteAsync(LlmPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // Own budget on top of the caller's token, so a model that never finishes is a 504 rather
        // than a connection the client eventually gives up on.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        try
        {
            using var response = await _http.PostAsJsonAsync(ChatPath, BuildRequest(prompt), timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Ollama chat returned {StatusCode} for model {Model}",
                    (int)response.StatusCode, _options.Model);

                return Result<LlmCompletion>.Failure(
                    $"LlmUnavailable: Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    "LlmUnavailable");
            }

            var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(timeout.Token);

            return ToCompletion(payload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up (request aborted); not ours to translate.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Ollama chat exceeded the {Timeout}s budget for model {Model}",
                _options.TimeoutSeconds, _options.Model);

            return Result<LlmCompletion>.Failure(
                $"LlmTimeout: generation exceeded {_options.TimeoutSeconds}s.", "LlmTimeout");
        }
        catch (Exception ex)
        {
            // Connection refused, DNS failure, model still downloading, malformed body.
            _logger.LogWarning(ex, "Ollama chat call failed for model {Model}", _options.Model);

            return Result<LlmCompletion>.Failure($"LlmUnavailable: {ex.Message}", "LlmUnavailable");
        }
    }

    private ChatRequest BuildRequest(LlmPrompt prompt) => new(
        _options.Model,
        [new ChatMessage("system", prompt.System), new ChatMessage("user", prompt.User)],
        Stream: false,
        new ChatOptions(_options.Temperature, _options.MaxAnswerTokens));

    /// <summary>
    /// A 200 with no content is the provider failing to answer, which for the caller is the same
    /// situation as it being down: 503, explicit and retryable.
    /// </summary>
    private Result<LlmCompletion> ToCompletion(ChatResponse? payload)
    {
        var text = payload?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Ollama chat returned an empty answer for model {Model}", _options.Model);

            return Result<LlmCompletion>.Failure(
                "LlmUnavailable: Ollama returned an empty answer.", "LlmUnavailable");
        }

        return Result<LlmCompletion>.Success(
            new LlmCompletion(text.Trim(), payload!.PromptTokens, payload.CompletionTokens));
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] ChatOptions Options);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatOptions(
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptTokens,
        [property: JsonPropertyName("eval_count")] int? CompletionTokens);
}
