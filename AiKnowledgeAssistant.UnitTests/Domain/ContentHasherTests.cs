using AiKnowledgeAssistant.Domain.Ingestion;

namespace AiKnowledgeAssistant.UnitTests.Domain;

public class ContentHasherTests
{
    [Fact]
    public void Hash_SameContent_ReturnsSameHash()
    {
        const string content = "The corporate handbook, chapter one.";

        Assert.Equal(ContentHasher.Hash(content), ContentHasher.Hash(content));
    }

    [Fact]
    public void Hash_DifferentContent_ReturnsDifferentHash()
    {
        var first = ContentHasher.Hash("The corporate handbook, chapter one.");
        var second = ContentHasher.Hash("The corporate handbook, chapter two.");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Hash_IsSensitiveToWhitespaceAndCase()
    {
        var baseline = ContentHasher.Hash("policy");

        Assert.NotEqual(baseline, ContentHasher.Hash("Policy"));
        Assert.NotEqual(baseline, ContentHasher.Hash("policy "));
    }

    [Fact]
    public void Hash_ReturnsLowercaseHexOf64Characters()
    {
        var hash = ContentHasher.Hash("anything");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }

    /// <summary>
    /// Golden value: pins the algorithm to plain SHA-256 over UTF-8 bytes, so a hash stored in
    /// Qdrant by an earlier build still matches after a refactor.
    /// </summary>
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("hello", "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    public void Hash_MatchesKnownSha256(string content, string expected)
    {
        Assert.Equal(expected, ContentHasher.Hash(content));
    }

    [Fact]
    public void Hash_HandlesNonAsciiContent()
    {
        var hash = ContentHasher.Hash("política de vacaciones — 日本語");

        Assert.Equal(64, hash.Length);
    }
}
