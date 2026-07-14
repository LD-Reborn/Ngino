using Microsoft.Extensions.Configuration;
using ReverseLlama.Protocol;

namespace ReverseLlama.Server;

internal sealed class ServerSettings
{
    public string StatusPath { get; init; } = ProtocolConstants.DefaultStatusPath;

    public string TunnelPath { get; init; } = ProtocolConstants.DefaultTunnelPath;

    public string? Token { get; init; }

    public int ChunkSize { get; init; } = 64 * 1024;

    public string? EmbeddingCachePath { get; init; }

    public string? ManagementDatabasePath { get; init; }

    public KeycloakSettings Keycloak { get; init; } = new();

    public static ServerSettings FromConfiguration(IConfiguration configuration)
    {
        return new ServerSettings
        {
            StatusPath = NormalizePath(Read(configuration, "ReverseLlama:StatusPath", "status-path") ?? ProtocolConstants.DefaultStatusPath),
            TunnelPath = NormalizePath(Read(configuration, "ReverseLlama:TunnelPath", "tunnel-path") ?? ProtocolConstants.DefaultTunnelPath),
            Token = Read(configuration, "ReverseLlama:Token", "token") ?? Environment.GetEnvironmentVariable("REVERSE_LLAMA_TOKEN"),
            ChunkSize = ReadInt(configuration, 64 * 1024, "ReverseLlama:ChunkSize", "chunk-size", "REVERSE_LLAMA_CHUNK_SIZE"),
            EmbeddingCachePath = Read(
                configuration,
                "ReverseLlama:EmbeddingCachePath",
                "embedding-cache-path",
                "REVERSE_LLAMA_EMBEDDING_CACHE_PATH"),
            ManagementDatabasePath = Read(
                configuration,
                "ReverseLlama:ManagementDatabasePath",
                "management-database-path",
                "REVERSE_LLAMA_MANAGEMENT_DATABASE_PATH"),
            Keycloak = new KeycloakSettings
            {
                Authority = Read(configuration, "Authentication:Keycloak:Authority", "REVERSE_LLAMA_KEYCLOAK_AUTHORITY"),
                ClientId = Read(configuration, "Authentication:Keycloak:ClientId", "REVERSE_LLAMA_KEYCLOAK_CLIENT_ID"),
                ClientSecret = Read(configuration, "Authentication:Keycloak:ClientSecret", "REVERSE_LLAMA_KEYCLOAK_CLIENT_SECRET"),
                RequireHttpsMetadata = ReadBool(
                    configuration,
                    true,
                    "Authentication:Keycloak:RequireHttpsMetadata",
                    "REVERSE_LLAMA_KEYCLOAK_REQUIRE_HTTPS_METADATA")
            }
        };
    }

    private static string? Read(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key] ?? Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int ReadInt(IConfiguration configuration, int fallback, params string[] keys)
    {
        var value = Read(configuration, keys);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static bool ReadBool(IConfiguration configuration, bool fallback, params string[] keys)
    {
        var value = Read(configuration, keys);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string NormalizePath(string path) =>
        path.StartsWith('/') ? path : $"/{path}";
}

internal sealed class KeycloakSettings
{
    public string? Authority { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public bool RequireHttpsMetadata { get; init; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
