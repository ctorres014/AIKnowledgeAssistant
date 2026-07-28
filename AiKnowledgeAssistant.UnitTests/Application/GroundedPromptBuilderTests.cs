using AiKnowledgeAssistant.Application.Rag;
using AiKnowledgeAssistant.Domain.Ingestion;
using AiKnowledgeAssistant.Domain.Rag;

namespace AiKnowledgeAssistant.UnitTests.Application;

public class GroundedPromptBuilderTests
{
    private readonly GroundedPromptBuilder _builder = new();

    private static RetrievedChunk Chunk(
        string title = "handbook-onboarding",
        int chunkIndex = 3,
        string text = "El onboarding de nuevos empleados se estructura en cinco jornadas.",
        float score = 0.82f) =>
        new($"c:/docs/{title}.md", SourceType.Markdown, title, chunkIndex, text, score);

    [Fact]
    public void Build_SystemBlockStatesTheThreeGroundingRules()
    {
        var prompt = _builder.Build("¿Cuánto dura el onboarding?", [Chunk()]);

        // Only from the context.
        Assert.Contains("ÚNICAMENTE", prompt.System, StringComparison.Ordinal);
        Assert.Contains("CONTEXTO", prompt.System, StringComparison.Ordinal);

        // Admit when the context falls short, without falling back on its own knowledge.
        Assert.Contains("no contiene la respuesta", prompt.System, StringComparison.Ordinal);
        Assert.Contains("no uses conocimiento propio", prompt.System, StringComparison.Ordinal);

        // Answer in the language of the question.
        Assert.Contains("mismo idioma", prompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UserBlockCarriesEveryChunkTextInFull()
    {
        var chunks = new[]
        {
            Chunk(title: "handbook", chunkIndex: 0, text: "Primer fragmento completo."),
            Chunk(title: "policies", chunkIndex: 7, text: "Segundo fragmento completo.")
        };

        var prompt = _builder.Build("¿Cuánto dura el onboarding?", chunks);

        Assert.Contains("Primer fragmento completo.", prompt.User, StringComparison.Ordinal);
        Assert.Contains("Segundo fragmento completo.", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NumbersChunksInSearchOrderWithTitleAndChunkIndex()
    {
        var chunks = new[]
        {
            Chunk(title: "handbook", chunkIndex: 0),
            Chunk(title: "policies", chunkIndex: 7),
            Chunk(title: "faq", chunkIndex: 2)
        };

        var prompt = _builder.Build("¿Cuánto dura el onboarding?", chunks);

        Assert.Contains("[1] handbook (fragmento 0)", prompt.User, StringComparison.Ordinal);
        Assert.Contains("[2] policies (fragmento 7)", prompt.User, StringComparison.Ordinal);
        Assert.Contains("[3] faq (fragmento 2)", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_KeepsTheSearchOrderRatherThanReorderingByScore()
    {
        var chunks = new[]
        {
            Chunk(title: "first", text: "Texto uno."),
            Chunk(title: "second", text: "Texto dos."),
            Chunk(title: "third", text: "Texto tres.")
        };

        var user = _builder.Build("¿Cuánto dura el onboarding?", chunks).User;

        Assert.True(
            user.IndexOf("Texto uno.", StringComparison.Ordinal) <
            user.IndexOf("Texto dos.", StringComparison.Ordinal));
        Assert.True(
            user.IndexOf("Texto dos.", StringComparison.Ordinal) <
            user.IndexOf("Texto tres.", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PutsTheQuestionLast()
    {
        var prompt = _builder.Build("¿Cuánto dura el onboarding?", [Chunk()]);

        Assert.StartsWith("CONTEXTO:", prompt.User, StringComparison.Ordinal);
        Assert.EndsWith("PREGUNTA: ¿Cuánto dura el onboarding?", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TrimsTheQuestion()
    {
        var prompt = _builder.Build("  ¿Cuánto dura el onboarding?\n", [Chunk()]);

        Assert.EndsWith("PREGUNTA: ¿Cuánto dura el onboarding?", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsAnEmptyContext()
    {
        Assert.Throws<ArgumentException>(() => _builder.Build("¿Cuánto dura el onboarding?", []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsABlankQuestion(string question)
    {
        Assert.Throws<ArgumentException>(() => _builder.Build(question, [Chunk()]));
    }
}
