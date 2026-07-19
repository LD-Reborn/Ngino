using Microsoft.Extensions.Configuration;
using ReverseLlama.Protocol;

namespace ReverseLlama.Server;

internal sealed class ServerSettings
{
    public string StatusPath { get; init; } = ProtocolConstants.DefaultStatusPath;

    public string TunnelPath { get; init; } = ProtocolConstants.DefaultTunnelPath;

    public string? Token { get; init; }

    public string? ClientToken { get; init; }

    public int ChunkSize { get; init; } = 64 * 1024;

    public string? EmbeddingCachePath { get; init; }

    public string? ManagementDatabasePath { get; init; }

    public bool SecureCookies { get; init; } = true;

    public KeycloakSettings Keycloak { get; init; } = new();

    public CorsSettings Cors { get; init; } = new();

    public static ServerSettings FromConfiguration(IConfiguration configuration)
    {
        return new ServerSettings
        {
            StatusPath = NormalizePath(Read(configuration, "ReverseLlama:StatusPath", "status-path") ?? ProtocolConstants.DefaultStatusPath),
            TunnelPath = NormalizePath(Read(configuration, "ReverseLlama:TunnelPath", "tunnel-path") ?? ProtocolConstants.DefaultTunnelPath),
            Token = Read(configuration, "ReverseLlama:Token", "token") ?? Environment.GetEnvironmentVariable("REVERSE_LLAMA_TOKEN"),
            ClientToken = Read(configuration, "ReverseLlama:ClientToken", "client-token") ?? Environment.GetEnvironmentVariable("REVERSE_LLAMA_CLIENT_TOKEN"),
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
            SecureCookies = ReadBool(configuration, true, "ReverseLlama:SecureCookies", "secure-cookies", "REVERSE_LLAMA_SECURE_COOKIES"),
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
            },
            Cors = new CorsSettings
            {
                AllowedOrigins = ReadStringArray(configuration, ["CORS:AllowedOrigins"]),
                AllowedMethods = ReadStringArray(configuration, ["CORS:AllowedMethods"]),
                AllowedHeaders = ReadStringArray(configuration, ["CORS:AllowedHeaders"]),
                AllowCredentials = ReadBool(configuration, false, "CORS:AllowCredentials")
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

    private static string[] ReadStringArray(IConfiguration configuration, params string[] keys)
    {
        var section = configuration.GetSection(keys[0]);
        var children = section.GetChildren().ToList();
        if (children.Count > 0)
        {
            return children.Select(c => c.Value!).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        }

        var value = Read(configuration, keys);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizePath(string path) =>
        path.StartsWith('/') ? path : $"/{path}";
}

internal sealed class CorsSettings
{
    public string[] AllowedOrigins { get; init; } = ["*"];

    public string[] AllowedMethods { get; init; } = ["*"];

    public string[] AllowedHeaders { get; init; } = ["*"];

    public bool AllowCredentials { get; init; }
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
