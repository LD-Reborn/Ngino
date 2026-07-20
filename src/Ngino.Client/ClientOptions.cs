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

    public int ChunkSize { get; init; } = 64 * 1024;

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
            Server = ReadUri(values, "server", "REVERSE_LLAMA_SERVER", "http://localhost:5001"),
            Upstream = ReadUri(values, "upstream", "REVERSE_LLAMA_UPSTREAM", "http://localhost:11434"),
            TunnelPath = NormalizePath(Read(values, "tunnel-path", "REVERSE_LLAMA_TUNNEL_PATH") ?? ProtocolConstants.DefaultTunnelPath),
            Token = Read(values, "token", "REVERSE_LLAMA_TOKEN"),
            ClientId = Read(values, "client-id", "REVERSE_LLAMA_CLIENT_ID") ?? Environment.MachineName.ToLowerInvariant(),
            ReconnectDelay = TimeSpan.FromSeconds(ReadInt(values, 5, "reconnect-delay", "REVERSE_LLAMA_RECONNECT_DELAY_SECONDS")),
            ChunkSize = ReadInt(values, 64 * 1024, "chunk-size", "REVERSE_LLAMA_CHUNK_SIZE")
        };
    }

    public static string Usage =>
        """
        Ngino.Client options:
          --server <url>             Server base URL, e.g. http://my-server:5050
          --upstream <url>           Local upstream URL, e.g. http://localhost:11434
          --token <value>            Optional token matching the server
          --client-id <name>         Identifies this machine on the server; defaults to the machine name
          --tunnel-path <path>       Defaults to /_ngino/tunnel
          --reconnect-delay <sec>    Defaults to 5
          --chunk-size <bytes>       Defaults to 65536
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

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for '{arg}'.");
            }

            values[keyValue[0]] = args[++i];
        }

        return values;
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
}
