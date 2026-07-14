using System.Net;
using System.Threading.Channels;

namespace ReverseLlama.Client;

internal sealed class ChannelHttpContent : HttpContent
{
    private readonly CancellationToken _cancellationToken;
    private readonly ChannelReader<byte[]> _reader;

    public ChannelHttpContent(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        _reader = reader;
        _cancellationToken = cancellationToken;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, _cancellationToken);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);

        await foreach (var chunk in _reader.ReadAllAsync(linkedCts.Token))
        {
            await stream.WriteAsync(chunk, linkedCts.Token);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
