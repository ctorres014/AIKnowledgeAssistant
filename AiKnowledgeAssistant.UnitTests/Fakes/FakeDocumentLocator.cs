using AiKnowledgeAssistant.Application.Abstractions;
using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.UnitTests.Fakes;

/// <summary>Locator that returns a fixed list of documents, or a fixed failure.</summary>
public sealed class FakeDocumentLocator : IDocumentLocator
{
    private readonly Result<IReadOnlyList<LocatedDocument>> _result;

    private FakeDocumentLocator(Result<IReadOnlyList<LocatedDocument>> result) => _result = result;

    public static FakeDocumentLocator Returning(IDocumentSource source, params string[] paths) =>
        new(Result<IReadOnlyList<LocatedDocument>>.Success(
            [.. paths.Select(p => new LocatedDocument(p, source))]));

    public static FakeDocumentLocator Failing(string error, string code) =>
        new(Result<IReadOnlyList<LocatedDocument>>.Failure(error, code));

    public Result<IReadOnlyList<LocatedDocument>> Locate(string path, string? sourceType) => _result;
}
