using System.Threading.Channels;
using Ngino.Protocol;

namespace Ngino.Server;

internal sealed class PendingProxyRequest
{
    private readonly Channel<byte[]> _body = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly TaskCompletionSource<TunnelMessage> _responseHeaders =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChannelReader<byte[]> Body => _body.Reader;

    public Task<TunnelMessage> WaitForHeadersAsync(CancellationToken cancellationToken) =>
        _responseHeaders.Task.WaitAsync(cancellationToken);

    public void SetResponseHeaders(TunnelMessage message) =>
        _responseHeaders.TrySetResult(message);

    public void AddBody(byte[] body)
    {
        if (body.Length > 0)
        {
            _body.Writer.TryWrite(body);
        }
    }

    public void Complete()
    {
        if (!_responseHeaders.Task.IsCompleted)
        {
            _responseHeaders.TrySetException(new InvalidOperationException("The client completed a response before sending response headers."));
        }

        _body.Writer.TryComplete();
    }

    public void Fail(string message) =>
        Fail(new InvalidOperationException(message));

    public void Fail(Exception exception)
    {
        _responseHeaders.TrySetException(exception);
        _body.Writer.TryComplete(exception);
    }
}
