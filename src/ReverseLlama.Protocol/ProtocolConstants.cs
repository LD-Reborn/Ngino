namespace ReverseLlama.Protocol;

public static class ProtocolConstants
{
    public const string DefaultStatusPath = "/_reverse-llama/status";
    public const string DefaultTunnelPath = "/_reverse-llama/tunnel";
    public const string TokenHeader = "X-Reverse-Llama-Token";
    public const string ClientIdHeader = "X-Reverse-Llama-Client-Id";
    public const string ReplacedCloseDescription = "reverse-llama-replaced";
}
