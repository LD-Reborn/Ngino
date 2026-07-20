namespace Ngino.Protocol;

public sealed class TunnelMessage
{
    public string Type { get; set; } = "";

    public string RequestId { get; set; } = "";

    public string? Method { get; set; }

    public string? PathAndQuery { get; set; }

    public bool HasBody { get; set; }

    public List<HeaderPair> Headers { get; set; } = [];

    public int? StatusCode { get; set; }

    public string? ReasonPhrase { get; set; }

    public byte[]? Body { get; set; }

    public List<string> Models { get; set; } = [];

    public List<string> ActiveModels { get; set; } = [];

    public string? Command { get; set; }

    public string? Model { get; set; }

    public string? PayloadJson { get; set; }

    public string? Error { get; set; }
}
