using System.Security.Cryptography;
using System.Text;

namespace AiKnowledgeAssistant.Domain.Ingestion;

/// <summary>
/// Derives the stable point ID of a chunk from its document and position, so re-ingesting a
/// document reuses the same IDs and can never duplicate points in the vector store.
/// </summary>
public static class ChunkIdFactory
{
    /// <summary>Namespace that scopes every chunk ID produced by this application.</summary>
    private static readonly Guid Namespace = new("3f2a1c64-6d5b-4a2f-9c1e-0b7d5f8a4e21");

    /// <summary>
    /// Deterministic GUID for <c>$"{sourceId}#{index}"</c>. RFC 4122 name-based layout
    /// (version 5 bits, RFC variant) but hashed with SHA-256 truncated to 16 bytes instead of
    /// SHA-1 — the spec asks for "UUIDv5-like" identity, not for interop with a v5 generator.
    /// </summary>
    public static Guid ForChunk(string sourceId, int index)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        var name = Encoding.UTF8.GetBytes($"{sourceId}#{index}");
        var namespaceBytes = Namespace.ToByteArray(bigEndian: true);

        Span<byte> input = stackalloc byte[namespaceBytes.Length + name.Length];
        namespaceBytes.CopyTo(input);
        name.CopyTo(input[namespaceBytes.Length..]);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);

        var id = hash[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x50); // version 5
        id[8] = (byte)((id[8] & 0x3F) | 0x80); // RFC 4122 variant

        return new Guid(id, bigEndian: true);
    }
}
