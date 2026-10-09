using System.Net;
using System.Threading.Channels;

namespace Ngino.Client;

internal sealed class ChannelHttpContent : HttpContent
{
    private readonly CancellationToken _cancellationToken;
    private readonly ChannelReader<byte[]> _reader;
    private readonly long _contentLength;

    public ChannelHttpContent(ChannelReader<byte[]> reader, CancellationToken cancellationToken, long contentLength = -1)
    {
        _reader = reader;
        _cancellationToken = cancellationToken;
        _contentLength = contentLength;
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
        if (_contentLength >= 0)
        {
            length = _contentLength;
            return true;
        }

        length = -1;
        return false;
    }
}
