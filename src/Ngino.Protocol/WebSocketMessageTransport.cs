using System.Net.WebSockets;
using System.Text.Json;

namespace Ngino.Protocol;

public static class WebSocketMessageTransport
{
    private const int BufferSize = 64 * 1024;
    private const int MaxMessageSize = 128 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task SendAsync(WebSocket socket, TunnelMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    public static async Task<TunnelMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        using var payload = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException("Only text WebSocket messages are supported.");
            }

            if (result.Count > 0)
            {
                payload.Write(buffer.AsSpan(0, result.Count));
            }

            if (payload.Length > MaxMessageSize)
            {
                throw new InvalidOperationException($"Tunnel message exceeded {MaxMessageSize} bytes.");
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        payload.Position = 0;
        var message = await JsonSerializer.DeserializeAsync<TunnelMessage>(payload, JsonOptions, cancellationToken);
        return message ?? throw new InvalidOperationException("Received an empty tunnel message.");
    }
}
