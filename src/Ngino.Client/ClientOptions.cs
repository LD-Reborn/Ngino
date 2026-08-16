using Ngino.Protocol;

namespace Ngino.Client;

internal sealed class ClientOptions
{
    public Uri Server { get; init; } = new("http://localhost:5001");

    public Uri Upstream { get; init; } = new("http://localhost:11434");

    public string TunnelPath { get; init; } = ProtocolConstants.DefaultTunnelPath;

    public string? Token { get; init; }

    public string ClientId { get; init; } = Environment.MachineName.ToLowerInvariant();

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    public bool InsecureSkipTlsVerify { get; init; }

    public bool UseLlamaCppViaDocker { get; init; }

    public string? UseOllamaModelsPath { get; init; }

    public string? LlamaCppDockerImage { get; init; }

    public int LlamaCppBasePort { get; init; } = 8081;

    public int? LlamaCppParallel { get; init; }

    public TimeSpan LlamaCppFallbackCooldown { get; init; } = TimeSpan.FromMinutes(3);

    public string? LogDirectory { get; init; }

    public Uri TunnelUri
    {
        get
        {
            var builder = new UriBuilder(Server);
            builder.Scheme = builder.Scheme.ToLowerInvariant() switch
            {
                "http" => "ws",
                "https" => "wss",
                "ws" => "ws",
                "wss" => "wss",
                var unsupported => throw new InvalidOperationException($"Unsupported server URI scheme '{unsupported}'. Use http, https, ws, or wss.")
            };

            if (string.IsNullOrWhiteSpace(builder.Path) || builder.Path == "/")
            {
                builder.Path = NormalizePath(TunnelPath);
            }

            return builder.Uri;
        }
    }

    public static ClientOptions Parse(string[] args)
    {
        var values = ParseArgs(args);

        return new ClientOptions
        {
            Server = ReadUri(values, "server", "NGINO_SERVER", "http://localhost:5001"),
            Upstream = ReadUri(values, "upstream", "NGINO_UPSTREAM", "http://localhost:11434"),
            TunnelPath = NormalizePath(Read(values, "tunnel-path", "NGINO_TUNNEL_PATH") ?? ProtocolConstants.DefaultTunnelPath),
            Token = Read(values, "token", "NGINO_TOKEN"),
            ClientId = Read(values, "client-id", "NGINO_CLIENT_ID") ?? Environment.MachineName.ToLowerInvariant(),
            ReconnectDelay = TimeSpan.FromSeconds(ReadInt(values, 5, "reconnect-delay", "NGINO_RECONNECT_DELAY_SECONDS")),
            InsecureSkipTlsVerify = ReadBool(values, false, "insecure-skip-tls-verify", "NGINO_INSECURE_SKIP_TLS_VERIFY"),
            UseLlamaCppViaDocker = ReadBool(values, false, "use-llama-cpp-via-docker", "NGINO_USE_LLAMA_CPP_VIA_DOCKER"),
            UseOllamaModelsPath = NormalizeDirectoryPath(Read(values, "use-ollama-models-path", "NGINO_USE_OLLAMA_MODELS_PATH")),
            LlamaCppDockerImage = Read(values, "llama-cpp-docker-image", "NGINO_LLAMA_CPP_DOCKER_IMAGE"),
            LlamaCppBasePort = ReadInt(values, 8081, "llama-cpp-base-port", "NGINO_LLAMA_CPP_BASE_PORT"),
            LlamaCppParallel = ReadOptionalInt(values, "llama-cpp-parallel", "NGINO_LLAMA_CPP_PARALLEL"),
            LlamaCppFallbackCooldown = TimeSpan.FromSeconds(ReadInt(values, 180, "llama-cpp-fallback-cooldown", "NGINO_LLAMA_CPP_FALLBACK_COOLDOWN_SECONDS")),
            LogDirectory = NormalizeDirectoryPath(Read(values, "log-dir", "NGINO_LOG_DIR"))
        };
    }

    public static string Usage =>
        """
        Ngino.Client options:
          --server <url>                 Server base URL, e.g. http://my-server:5050
          --upstream <url>               Local upstream URL, e.g. http://localhost:11434
          --token <value>                Optional token matching the server
          --client-id <name>             Identifies this machine on the server; defaults to the machine name
          --tunnel-path <path>           Defaults to /_ngino/tunnel
          --reconnect-delay <sec>        Defaults to 5
          --insecure-skip-tls-verify     Disable server TLS certificate validation (unsafe)
          --use-llama-cpp-via-docker     Use llama.cpp via Docker for inference instead of Ollama
          --use-ollama-models-path <dir> Path to Ollama models directory (manifests/blobs), required with --use-llama-cpp-via-docker
          --llama-cpp-docker-image <img> llama.cpp Docker image; defaults to auto-detected (rocm/cuda/cpu)
          --llama-cpp-base-port <num>    Base port for llama.cpp containers; defaults to 8081
          --llama-cpp-parallel <num>     llama.cpp parallel slots per container; if unset, llama.cpp's own default is used
          --llama-cpp-fallback-cooldown <sec>
                                         Seconds before llama.cpp is retried after a failed container start; defaults to 180
          --log-dir <dir>                Directory for log files; defaults to <app dir>/Logs
        """;

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            var keyValue = arg[2..].Split('=', 2);
            if (keyValue.Length == 2)
            {
                values[keyValue[0]] = keyValue[1];
                continue;
            }

            if (IsBoolFlag(keyValue[0]))
            {
                values[keyValue[0]] = "true";
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for '{arg}'.");
            }

            values[keyValue[0]] = args[++i];
        }

        return values;
    }

    private static bool IsBoolFlag(string key)
    {
        return key switch
        {
            "insecure-skip-tls-verify" => true,
            "use-llama-cpp-via-docker" => true,
            _ => false
        };
    }

    private static string? Read(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int ReadInt(Dictionary<string, string> values, int fallback, params string[] keys)
    {
        var value = Read(values, keys);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int? ReadOptionalInt(Dictionary<string, string> values, params string[] keys)
    {
        var value = Read(values, keys);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static bool ReadBool(Dictionary<string, string> values, bool fallback, params string[] keys)
    {
        var value = Read(values, keys);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static Uri ReadUri(Dictionary<string, string> values, string key, string envKey, string fallback)
    {
        var value = Read(values, key, envKey) ?? fallback;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"'{value}' is not an absolute URI.");
        }

        return uri;
    }

    private static string NormalizePath(string path) =>
        path.StartsWith('/') ? path : $"/{path}";

    private static string? NormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }
}
