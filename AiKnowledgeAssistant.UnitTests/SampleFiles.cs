namespace AiKnowledgeAssistant.UnitTests;

/// <summary>
/// Locates the versioned sample corpus copied next to the test assembly.
/// </summary>
/// <remarks>
/// <c>Docs/</c> holds one clean file per supported format, <c>Markdown/</c> holds exactly two
/// <c>.md</c> files (the folder the endpoint tests ingest), and <c>Edge/</c> holds the awkward
/// cases: byte order marks, an empty file, an unsupported extension and a corrupt PDF.
/// </remarks>
public static class SampleFiles
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "Samples");

    public static string Docs { get; } = Path.Combine(Root, "Docs");

    public static string MarkdownOnly { get; } = Path.Combine(Root, "Markdown");

    public static string Edge { get; } = Path.Combine(Root, "Edge");

    public static string InDocs(string fileName) => Path.Combine(Docs, fileName);

    public static string InEdge(string fileName) => Path.Combine(Edge, fileName);
}
