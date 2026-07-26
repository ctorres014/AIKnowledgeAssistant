using Microsoft.Extensions.Configuration;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Resolves the Ollama base address from configuration.
/// </summary>
/// <remarks>
/// Aspire injects a connection string per referenced resource, either as a bare URL or in
/// <c>Endpoint=http://host:port;Model=...</c> form. The embedding <em>model</em> resource is
/// preferred over the bare server, so the client points at whatever the app host actually pulled;
/// with neither configured we fall back to the service-discovery name, which the ServiceDefaults
/// handler resolves inside the app host.
/// </remarks>
public static class OllamaEndpoint
{
    /// <summary>Connection name of the embedding model resource in the app host.</summary>
    public const string ModelResourceName = "embedding";

    /// <summary>Connection / service name of the Ollama server resource in the app host.</summary>
    public const string ResourceName = "ollama";

    private const string Fallback = "http://ollama";

    public static Uri Resolve(IConfiguration configuration)
    {
        var endpoint =
            Parse(configuration.GetConnectionString(ModelResourceName)) ??
            Parse(configuration.GetConnectionString(ResourceName)) ??
            Fallback;

        return new Uri(endpoint);
    }

    private static string? Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        if (Uri.TryCreate(connectionString.Trim(), UriKind.Absolute, out _))
        {
            return connectionString.Trim();
        }

        // Keyed form: "Endpoint=http://host:port;Key=..."
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();

            if (string.Equals(key, "Endpoint", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                return value;
            }
        }

        return null;
    }
}
