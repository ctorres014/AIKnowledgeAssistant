using Microsoft.Extensions.Configuration;

namespace AiKnowledgeAssistant.Infrastructure.Ingestion;

/// <summary>
/// Resolves the Ollama base address from configuration.
/// </summary>
/// <remarks>
/// Aspire injects <c>ConnectionStrings:ollama</c> for the referenced resource, either as a bare URL
/// or in <c>Endpoint=http://host:port</c> form. When nothing is configured we fall back to the
/// service-discovery name, which the ServiceDefaults handler resolves inside the Aspire app host.
/// </remarks>
public static class OllamaEndpoint
{
    /// <summary>Connection / service name of the Ollama resource in the app host.</summary>
    public const string ResourceName = "ollama";

    private const string Fallback = "http://ollama";

    public static Uri Resolve(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ResourceName);

        return new Uri(Parse(connectionString) ?? Fallback);
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
