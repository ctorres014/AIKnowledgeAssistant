using System.Net;
using System.Text;
using System.Text.Json;
using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Infrastructure.Rag;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace AiKnowledgeAssistant.UnitTests.Infrastructure;

public class OllamaLlmClientTests
{
    private static readonly LlmPrompt Prompt = new("Responde solo con el contexto.", "CONTEXTO:\n...\n\nPREGUNTA: ¿?");

    private readonly List<string> _bodies = [];

    /// <summary>Mocked transport: <paramref name="respond"/> decides what (and how slowly) to answer.</summary>
    private OllamaLlmClient CreateClient(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        RagOptions? options = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken ct) =>
            {
                _bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                return await respond(ct);
            });

        var http = new HttpClient(handler.Object) { BaseAddress = new Uri("http://ollama:11434/") };

        return new OllamaLlmClient(
            http,
            Options.Create(options ?? new RagOptions()),
            NullLogger<OllamaLlmClient>.Instance);
    }

    private static Task<HttpResponseMessage> Ok(
        string content = "El onboarding dura cinco jornadas.",
        int? promptTokens = 412,
        int? completionTokens = 37)
    {
        var body = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content },
            prompt_eval_count = promptTokens,
            eval_count = completionTokens
        });

        return Task.FromResult(Json(HttpStatusCode.OK, body));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CompleteAsync_ReturnsTheGeneratedText()
    {
        var client = CreateClient(_ => Ok());

        var result = await client.CompleteAsync(Prompt, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("El onboarding dura cinco jornadas.", result.Value!.Text);
        Assert.Equal(412, result.Value.PromptTokens);
        Assert.Equal(37, result.Value.CompletionTokens);
    }

    [Fact]
    public async Task CompleteAsync_SendsTheConfiguredModelPromptAndGenerationOptions()
    {
        var options = new RagOptions { Model = "qwen2.5:7b", Temperature = 0.2f, MaxAnswerTokens = 500 };
        var client = CreateClient(_ => Ok(), options);

        await client.CompleteAsync(Prompt, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(_bodies));
        var root = body.RootElement;

        Assert.Equal("qwen2.5:7b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.2f, root.GetProperty("options").GetProperty("temperature").GetSingle());
        Assert.Equal(500, root.GetProperty("options").GetProperty("num_predict").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(Prompt.System, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(Prompt.User, messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_ExposesTheConfiguredModel()
    {
        var client = CreateClient(_ => Ok(), new RagOptions { Model = "llama3.2:3b" });

        Assert.Equal("llama3.2:3b", client.Model);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CompleteAsync_TurnsAnHttpErrorIntoLlmUnavailable(HttpStatusCode status)
    {
        var client = CreateClient(_ => Task.FromResult(Json(status, "{\"error\":\"model not found\"}")));

        var result = await client.CompleteAsync(Prompt, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("LlmUnavailable", result.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_TurnsAConnectionFailureIntoLlmUnavailable()
    {
        var client = CreateClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.CompleteAsync(Prompt, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("LlmUnavailable", result.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_TurnsAnEmptyAnswerIntoLlmUnavailable()
    {
        var client = CreateClient(_ => Ok(content: "   "));

        var result = await client.CompleteAsync(Prompt, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("LlmUnavailable", result.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_TurnsAGenerationSlowerThanTheBudgetIntoLlmTimeout()
    {
        var client = CreateClient(
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return await Ok();
            },
            new RagOptions { TimeoutSeconds = 1 });

        var result = await client.CompleteAsync(Prompt, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("LlmTimeout", result.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotSwallowCallerCancellation()
    {
        using var cts = new CancellationTokenSource();

        var client = CreateClient(async ct =>
        {
            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return await Ok();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CompleteAsync(Prompt, cts.Token));
    }
}
