using System.Security.Cryptography;
using System.Text;

namespace AiKnowledgeAssistant.Domain.Ingestion;

/// <summary>
/// Content fingerprint used for deduplication and idempotency: a document whose hash already sits
/// in the vector store is skipped, and a changed hash triggers a delete-and-reindex of that source.
/// </summary>
public static class ContentHasher
{
    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="content"/>.</summary>
    public static string Hash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(content), hash);

        return Convert.ToHexStringLower(hash);
    }
}
