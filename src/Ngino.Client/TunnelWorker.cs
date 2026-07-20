using Microsoft.Extensions.Hosting;

namespace ReverseLlama.Client;

internal sealed class TunnelWorker : BackgroundService
{
    private readonly TunnelClient _client;
    private readonly IHostApplicationLifetime _lifetime;

    public TunnelWorker(TunnelClient client, IHostApplicationLifetime lifetime)
    {
        _client = client;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _client.RunAsync(stoppingToken);
        }
        finally
        {
            // RunAsync only returns when cancelled or replaced by a newer client.
            // Stop gracefully (exit 0) so service recovery does not restart us
            // into a reconnect fight with the replacement.
            _lifetime.StopApplication();
        }
    }
}
