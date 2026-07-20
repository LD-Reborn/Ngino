using ReverseLlama.Protocol;

namespace ReverseLlama.Server;

internal sealed class PendingCommand
{
    private readonly TaskCompletionSource<TunnelMessage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TunnelMessage> WaitAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    public void Complete(TunnelMessage message) =>
        _completion.TrySetResult(message);

    public void Fail(string error) =>
        _completion.TrySetException(new InvalidOperationException(error));
}
