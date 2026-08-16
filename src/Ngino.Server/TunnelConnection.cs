using System.Collections.Concurrent;
using System.Net.WebSockets;
using Ngino.Protocol;

namespace Ngino.Server;

internal sealed class TunnelConnection
{
    private readonly ConcurrentDictionary<string, PendingCommand> _commands = new();
    private readonly Func<GroupAccess?>? _accessProvider;
    private readonly object _modelsLock = new();
    private readonly ConcurrentDictionary<string, PendingProxyRequest> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly WebSocket _socket;
    private readonly ILogger<TunnelConnection> _logger;
    private string[] _activeModels = [];
    private string[] _models = [];
    private DateTimeOffset? _modelsUpdatedAt;

    public TunnelConnection(
        string clientId,
        WebSocket socket,
        ILogger<TunnelConnection> logger,
        Func<GroupAccess?>? accessProvider = null)
    {
        ClientId = clientId;
        _socket = socket;
        _logger = logger;
        _accessProvider = accessProvider;
    }

    public string ClientId { get; }

    public string Id { get; } = Guid.NewGuid().ToString("n");

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public int PendingRequestCount => _pending.Count;

    public IReadOnlyList<string> Models
    {
        get
        {
            lock (_modelsLock)
            {
                return _models;
            }
        }
    }

    public IReadOnlyList<string> ActiveModels
    {
        get
        {
            lock (_modelsLock)
            {
                return _activeModels;
            }
        }
    }

    public DateTimeOffset? ModelsUpdatedAt
    {
        get
        {
            lock (_modelsLock)
            {
                return _modelsUpdatedAt;
            }
        }
    }

    public PendingProxyRequest RegisterPending(string requestId)
    {
        var pending = new PendingProxyRequest();

        if (!_pending.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException($"Request id {requestId} is already registered.");
        }

        return pending;
    }

    public void RemovePending(string requestId)
    {
        _pending.TryRemove(requestId, out _);
    }

    public bool HasModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var requested = model.Trim();

        foreach (var available in Models.Concat(ActiveModels))
        {
            if (ModelNamesMatch(requested, available))
            {
                return true;
            }
        }

        return false;
    }

    public void UpdateModels(IEnumerable<string> models, IEnumerable<string> activeModels)
    {
        var access = _accessProvider?.Invoke();
        var snapshot = models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(model => access is null || access.IsClientModelAllowed(ClientId, model))
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeSnapshot = activeModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(model => access is null || access.IsClientModelAllowed(ClientId, model))
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_modelsLock)
        {
            _models = snapshot;
            _activeModels = activeSnapshot;
            _modelsUpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task<TunnelMessage> SendModelCommandAsync(
        string command,
        string model,
        string? payloadJson,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command is required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.", nameof(model));
        }

        var requestId = Guid.NewGuid().ToString("n");
        var pending = new PendingCommand();

        if (!_commands.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException($"Command id {requestId} is already registered.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await SendAsync(
                new TunnelMessage
                {
                    Type = TunnelMessageTypes.ModelCommand,
                    RequestId = requestId,
                    Command = command,
                    Model = model,
                    PayloadJson = payloadJson
                },
                timeoutCts.Token);

            return await pending.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            _commands.TryRemove(requestId, out _);
        }
    }

    public async Task SendAsync(TunnelMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("The tunnel client is not connected.");
            }

            await WebSocketMessageTransport.SendAsync(_socket, message, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await WebSocketMessageTransport.ReceiveAsync(_socket, cancellationToken);
                if (message is null)
                {
                    break;
                }

                Dispatch(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tunnel receive loop failed.");
        }
        finally
        {
            FailAll("Tunnel client disconnected.");
        }
    }

    public async Task CloseAsync(string reason, string? closeDescription = null)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, closeDescription ?? reason, CancellationToken.None);
            }
        }
        catch
        {
        }
        finally
        {
            FailAll(reason);
        }
    }

    private void Dispatch(TunnelMessage message)
    {
        if (message.Type == TunnelMessageTypes.ModelSnapshot)
        {
            UpdateModels(message.Models, message.ActiveModels);
            _logger.LogInformation(
                "Tunnel client {ClientId} reported {ModelCount} listed and {ActiveModelCount} active model(s).",
                ClientId,
                Models.Count,
                ActiveModels.Count);
            return;
        }

        if (message.Type == TunnelMessageTypes.ModelCommandResult)
        {
            if (_commands.TryRemove(message.RequestId, out var pendingCommand))
            {
                if (!string.IsNullOrWhiteSpace(message.Error))
                {
                    pendingCommand.Fail(message.Error);
                }
                else
                {
                    pendingCommand.Complete(message);
                }
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(message.RequestId))
        {
            _logger.LogDebug("Ignoring tunnel message without a request id: {MessageType}", message.Type);
            return;
        }

        if (!_pending.TryGetValue(message.RequestId, out var pending))
        {
            _logger.LogDebug("Ignoring tunnel message for unknown request {RequestId}: {MessageType}", message.RequestId, message.Type);
            return;
        }

        switch (message.Type)
        {
            case TunnelMessageTypes.HttpResponseHeaders:
                pending.SetResponseHeaders(message);
                break;

            case TunnelMessageTypes.HttpResponseBody:
                pending.AddBody(message.Body ?? []);
                break;

            case TunnelMessageTypes.HttpResponseComplete:
                pending.Complete();
                break;

            case TunnelMessageTypes.Error:
                pending.Fail(message.Error ?? "The tunnel client reported an error.");
                break;

            default:
                _logger.LogDebug("Ignoring unsupported tunnel message type from client: {MessageType}", message.Type);
                break;
        }
    }

    private static bool ModelNamesMatch(string requested, string available) =>
        string.Equals(requested, available, StringComparison.OrdinalIgnoreCase)
        || string.Equals(StripLatestTag(requested), StripLatestTag(available), StringComparison.OrdinalIgnoreCase);

    private static string StripLatestTag(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? model[..^":latest".Length]
            : model;

    private void FailAll(string reason)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                pending.Fail(reason);
            }
        }

        foreach (var pair in _commands.ToArray())
        {
            if (_commands.TryRemove(pair.Key, out var pending))
            {
                pending.Fail(reason);
            }
        }
    }
}
