using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using ReverseLlama.Protocol;

namespace ReverseLlama.Server;

internal sealed class TunnelHub
{
    private readonly ConcurrentDictionary<string, TunnelConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TunnelHub> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private long _roundRobinCounter;

    public TunnelHub(ILogger<TunnelHub> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public bool HasClient => _connections.Values.Any(connection => connection.IsOpen);

    public int PendingRequestCount => _connections.Values.Sum(connection => connection.PendingRequestCount);

    public TunnelConnection? Get(string clientId) =>
        _connections.TryGetValue(clientId, out var connection) && connection.IsOpen ? connection : null;

    public TunnelConnection? SelectBest(string? model, Func<string, bool>? isAvailable = null)
    {
        var allOpen = _connections.Values
            .Where(connection => connection.IsOpen)
            .Where(connection => isAvailable?.Invoke(connection.ClientId) ?? true)
            .ToList();

        if (allOpen.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            var withModel = allOpen.Where(connection => connection.HasModel(model)).ToList();
            if (withModel.Count > 0)
            {
                return PickBest(withModel);
            }
        }

        return PickBest(allOpen);
    }

    private TunnelConnection PickBest(List<TunnelConnection> candidates)
    {
        var tick = (int)(Interlocked.Increment(ref _roundRobinCounter) & 0x7FFFFFFF);

        return candidates
            .Select((connection, index) => (connection, index))
            .OrderBy(x => x.connection.PendingRequestCount)
            .ThenBy(x => (tick + x.index) % candidates.Count)
            .First()
            .connection;
    }

    /// <summary>The only open connection, or null when zero or more than one client is connected.</summary>
    public TunnelConnection? Single
    {
        get
        {
            TunnelConnection? single = null;

            foreach (var connection in _connections.Values)
            {
                if (!connection.IsOpen)
                {
                    continue;
                }

                if (single is not null)
                {
                    return null;
                }

                single = connection;
            }

            return single;
        }
    }

    public IReadOnlyList<TunnelClientSnapshot> ClientSnapshots =>
        _connections.Values
            .Where(connection => connection.IsOpen)
            .OrderBy(connection => connection.ClientId, StringComparer.OrdinalIgnoreCase)
            .Select(connection => new TunnelClientSnapshot(
                connection.ClientId,
                connection.PendingRequestCount,
                connection.Models,
                connection.ActiveModels,
                connection.ModelsUpdatedAt))
            .ToList();

    public IReadOnlyList<object> ClientsSnapshot =>
        ClientSnapshots
            .Select(client => (object)new
            {
                id = client.Id,
                pendingRequests = client.PendingRequests,
                models = client.Models,
                activeModels = client.ActiveModels,
                modelsUpdatedAt = client.ModelsUpdatedAt
            })
            .ToList();

    public async Task AcceptAsync(string clientId, WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new TunnelConnection(clientId, socket, _loggerFactory.CreateLogger<TunnelConnection>());

        TunnelConnection? previous = null;
        _connections.AddOrUpdate(
            clientId,
            connection,
            (_, existing) =>
            {
                previous = existing;
                return connection;
            });

        if (previous is not null)
        {
            _logger.LogInformation(
                "Replacing existing tunnel client {ClientId} ({ConnectionId}) with {NewConnectionId}.",
                clientId, previous.Id, connection.Id);
            await previous.CloseAsync("A newer tunnel client connected.", ProtocolConstants.ReplacedCloseDescription);
        }

        _logger.LogInformation("Tunnel client {ClientId} ({ConnectionId}) connected.", clientId, connection.Id);

        try
        {
            await connection.RunReceiveLoopAsync(cancellationToken);
        }
        finally
        {
            _connections.TryRemove(new KeyValuePair<string, TunnelConnection>(clientId, connection));
            await connection.CloseAsync("Tunnel closed.");
            _logger.LogInformation("Tunnel client {ClientId} ({ConnectionId}) disconnected.", clientId, connection.Id);
        }
    }
}

internal sealed record TunnelClientSnapshot(
    string Id,
    int PendingRequests,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> ActiveModels,
    DateTimeOffset? ModelsUpdatedAt);
