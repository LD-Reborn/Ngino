using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ngino.Protocol;

namespace Ngino.Client;

internal sealed class TunnelClient
{
    private static readonly TimeSpan ModelRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ModelRefreshTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string EmbeddingWarmupInput = "Ngino warmup";

    private readonly ConcurrentDictionary<string, UpstreamRequest> _activeRequests = new();
    private readonly HttpClient _httpClient;
    private readonly ClientOptions _options;
    private readonly ILogger<TunnelClient> _logger;
    private readonly object _modelSnapshotLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly LlamaCppManager? _llamaCppManager;
    private List<string> _lastActiveModels = [];
    private List<string> _lastModels = [];

    public TunnelClient(ClientOptions options, ILogger<TunnelClient>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<TunnelClient>.Instance;
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (_options.UseLlamaCppViaDocker)
        {
            if (string.IsNullOrWhiteSpace(_options.UseOllamaModelsPath))
            {
                throw new InvalidOperationException(
                    "--use-ollama-models-path is required when --use-llama-cpp-via-docker is set.");
            }

            _llamaCppManager = new LlamaCppManager(
                _options.UseOllamaModelsPath,
                _options.LlamaCppDockerImage,
                _options.LlamaCppBasePort,
                _logger);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_llamaCppManager is not null)
        {
            _logger.LogInformation("Testing Docker availability...");
            var dockerAvailable = await _llamaCppManager.TestDockerAsync();
            if (!dockerAvailable)
            {
                _logger.LogWarning("Docker is not available. llama.cpp via Docker will not work.");
            }
            else
            {
                _logger.LogInformation("Docker is available. Using llama.cpp image: {Image}", _llamaCppManager.DockerImage);
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            if (_options.InsecureSkipTlsVerify)
            {
                socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            }

            if (!string.IsNullOrWhiteSpace(_options.Token))
            {
                socket.Options.SetRequestHeader(ProtocolConstants.TokenHeader, _options.Token);
            }

            socket.Options.SetRequestHeader(ProtocolConstants.ClientIdHeader, _options.ClientId);

            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? modelRefreshTask = null;

            try
            {
                _logger.LogInformation("Connecting to {TunnelUri}...", _options.TunnelUri);
                await socket.ConnectAsync(_options.TunnelUri, cancellationToken);
                _logger.LogInformation("Tunnel connected.");

                modelRefreshTask = RefreshModelsLoopAsync(socket, connectionCts.Token);
                await ReceiveLoopAsync(socket, connectionCts.Token);

                if (socket.CloseStatusDescription == ProtocolConstants.ReplacedCloseDescription)
                {
                    _logger.LogWarning("This client was replaced by a newer tunnel client. Exiting.");
                    return;
                }

                _logger.LogInformation("Tunnel closed by server ({Reason}).", socket.CloseStatusDescription ?? "no reason given");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Tunnel disconnected: {Message}", exception.Message);
            }
            finally
            {
                connectionCts.Cancel();
                if (modelRefreshTask is not null)
                {
                    try
                    {
                        await modelRefreshTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                CancelAllActiveRequests();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Reconnecting in {Seconds:0.#} seconds...", _options.ReconnectDelay.TotalSeconds);
                await Task.Delay(_options.ReconnectDelay, cancellationToken);
            }
        }
    }

    private async Task RefreshModelsLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshModelsOnceAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Failed to report upstream model list: {Message}", exception.Message);
            }

            await Task.Delay(ModelRefreshInterval, cancellationToken);
        }
    }

    private async Task RefreshModelsOnceAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var modelsTask = TryRefreshModelListAsync(GetUpstreamModelsAsync, "listed", cancellationToken);
        var activeModelsTask = TryRefreshModelListAsync(GetActiveUpstreamModelsAsync, "active", cancellationToken);

        await Task.WhenAll(modelsTask, activeModelsTask);

        var snapshot = UpdateCachedModelSnapshot(modelsTask.Result, activeModelsTask.Result);

        await SendAsync(
            socket,
            new TunnelMessage
            {
                Type = TunnelMessageTypes.ModelSnapshot,
                Models = snapshot.Models,
                ActiveModels = snapshot.ActiveModels
            },
            cancellationToken);

        _logger.LogInformation(
            "Reported {ModelCount} listed and {ActiveModelCount} active upstream model(s).",
            snapshot.Models.Count,
            snapshot.ActiveModels.Count);
    }

    private async Task<List<string>> GetUpstreamModelsAsync(CancellationToken cancellationToken)
    {
        if (_llamaCppManager is not null)
        {
            var models = _llamaCppManager.DiscoverModelsWithBlob();
            return models
                .Select(m => m.OllamaName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.Upstream, "/api/tags"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ExtractModelNames(document.RootElement);
    }

    private async Task<List<string>> GetActiveUpstreamModelsAsync(CancellationToken cancellationToken)
    {
        if (_llamaCppManager is not null)
        {
            var models = _llamaCppManager.DiscoverModelsWithBlob();
            return models
                .Where(m => _llamaCppManager.IsModelActive(m.OllamaName))
                .Select(m => m.OllamaName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.Upstream, "/api/ps"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Ollama /api/ps returned {StatusCode}; active model list will be empty.", response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ExtractModelNames(document.RootElement);
    }

    internal static List<string> ExtractModelNames(JsonElement root)
    {
        var models = new List<string>();

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("models", out var ollamaModels)
            && ollamaModels.ValueKind == JsonValueKind.Array)
        {
            AddModelNames(models, ollamaModels, "name");
            AddModelNames(models, ollamaModels, "model");
        }

        return NormalizeModelNames(models);
    }

    private static void AddModelNames(List<string> models, JsonElement array, string propertyName)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(propertyName, out var model)
                && model.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(model.GetString()))
            {
                models.Add(model.GetString()!);
            }
        }
    }

    private async Task<List<string>?> TryRefreshModelListAsync(
        Func<CancellationToken, Task<List<string>>> refresh,
        string listName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            refreshCts.CancelAfter(ModelRefreshTimeout);

            return await refresh(refreshCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out refreshing {ModelListName} upstream model list.", listName);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Failed to refresh {ModelListName} upstream model list: {Message}",
                listName,
                exception.Message);
            return null;
        }
    }

    private (List<string> Models, List<string> ActiveModels) UpdateCachedModelSnapshot(
        List<string>? models,
        List<string>? activeModels)
    {
        lock (_modelSnapshotLock)
        {
            if (models is not null)
            {
                _lastModels = models;
            }

            if (activeModels is not null)
            {
                _lastActiveModels = activeModels;
            }

            return (
                [.. _lastModels],
                [.. _lastActiveModels]);
        }
    }

    private static List<string> NormalizeModelNames(IEnumerable<string> models) =>
        models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await WebSocketMessageTransport.ReceiveAsync(socket, cancellationToken);
            if (message is null)
            {
                break;
            }

            await DispatchAsync(socket, message, cancellationToken);
        }
    }

    private Task DispatchAsync(ClientWebSocket socket, TunnelMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case TunnelMessageTypes.HttpRequest:
                StartRequest(socket, message, cancellationToken);
                break;

            case TunnelMessageTypes.HttpRequestBody:
                if (_activeRequests.TryGetValue(message.RequestId, out var requestWithBody))
                {
                    requestWithBody.AddBody(message.Body ?? []);
                }
                break;

            case TunnelMessageTypes.HttpRequestComplete:
                if (_activeRequests.TryGetValue(message.RequestId, out var completedRequest))
                {
                    completedRequest.CompleteBody();
                }
                break;

            case TunnelMessageTypes.Cancel:
                if (_activeRequests.TryRemove(message.RequestId, out var cancelledRequest))
                {
                    cancelledRequest.Cancel();
                }
                break;

            case TunnelMessageTypes.ModelCommand:
                _ = Task.Run(() => RunModelCommandAsync(socket, message, cancellationToken), cancellationToken);
                break;
        }

        return Task.CompletedTask;
    }

    private async Task RunModelCommandAsync(ClientWebSocket socket, TunnelMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteModelCommandAsync(message, cancellationToken);
            if (response.StatusCode is >= 200 and < 300)
            {
                await RefreshModelsOnceAsync(socket, cancellationToken);
            }

            await SendAsync(socket, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SendAsync(
                socket,
                new TunnelMessage
                {
                    Type = TunnelMessageTypes.ModelCommandResult,
                    RequestId = message.RequestId,
                    Error = exception.Message
                },
                CancellationToken.None);
        }
    }

    private async Task<TunnelMessage> ExecuteModelCommandAsync(TunnelMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.RequestId))
        {
            throw new InvalidOperationException("Model command is missing a request id.");
        }

        if (string.IsNullOrWhiteSpace(message.Model))
        {
            throw new InvalidOperationException("Model command is missing a model name.");
        }

        if (_llamaCppManager is not null)
        {
            return await ExecuteModelCommandWithLlamaCppAsync(message, cancellationToken);
        }

        using var request = BuildModelCommandRequest(message);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (ShouldRetryModelCommandWithEmbedding(message.Command, response, body))
        {
            using var embeddingRequest = BuildEmbeddingModelCommandRequest(_options.Upstream, message.Command, message.Model);
            using var embeddingResponse = await _httpClient.SendAsync(embeddingRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var embeddingBody = await embeddingResponse.Content.ReadAsByteArrayAsync(cancellationToken);

            return BuildModelCommandResult(message.RequestId, embeddingResponse, embeddingBody);
        }

        return BuildModelCommandResult(message.RequestId, response, body);
    }

    private async Task<TunnelMessage> ExecuteModelCommandWithLlamaCppAsync(
        TunnelMessage message, CancellationToken cancellationToken)
    {
        var modelName = message.Model?.Trim();
        var normalizedCommand = NormalizeModelCommand(message.Command);

        switch (normalizedCommand)
        {
            case "load":
            {
                var models = _llamaCppManager!.DiscoverModelsWithBlob();
                var model = models.FirstOrDefault(m =>
                    string.Equals(m.OllamaName, modelName, StringComparison.OrdinalIgnoreCase));

                if (model is null)
                {
                    return new TunnelMessage
                    {
                        Type = TunnelMessageTypes.ModelCommandResult,
                        RequestId = message.RequestId,
                        StatusCode = 404,
                        Error = $"Model '{modelName}' not found in Ollama models path."
                    };
                }

                var started = await _llamaCppManager.StartModelContainerAsync(model, cancellationToken);
                if (!started)
                {
                    return new TunnelMessage
                    {
                        Type = TunnelMessageTypes.ModelCommandResult,
                        RequestId = message.RequestId,
                        StatusCode = 500,
                        Error = $"Failed to start llama.cpp container for model '{modelName}'."
                    };
                }

                return BuildModelCommandResult(message.RequestId, 200, "OK", []);
            }

            case "unload":
            {
                var stopped = await _llamaCppManager!.StopModelContainerAsync(modelName!, cancellationToken);
                if (!stopped)
                {
                    return new TunnelMessage
                    {
                        Type = TunnelMessageTypes.ModelCommandResult,
                        RequestId = message.RequestId,
                        StatusCode = 404,
                        Error = $"No running llama.cpp container for model '{modelName}'."
                    };
                }

                return BuildModelCommandResult(message.RequestId, 200, "OK", []);
            }

            case "pull":
            case "delete":
            {
                using var request = BuildModelCommandRequest(_options.Upstream, message.Command, message.Model);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return BuildModelCommandResult(message.RequestId, response, body);
            }

            case "show":
            {
                var models = _llamaCppManager!.DiscoverModelsWithBlob();
                var model = models.FirstOrDefault(m =>
                    string.Equals(m.OllamaName, modelName, StringComparison.OrdinalIgnoreCase));

                if (model is null)
                {
                    return new TunnelMessage
                    {
                        Type = TunnelMessageTypes.ModelCommandResult,
                        RequestId = message.RequestId,
                        StatusCode = 404,
                        Error = $"Model '{modelName}' not found in Ollama models path."
                    };
                }

                var showResponse = new
                {
                    modelfile = $"# llama.cpp via Docker\nFROM {model.BlobDigest}\n",
                    details = new
                    {
                        format = "gguf",
                        family = "llama",
                        parameter_size = "",
                        quantization_level = ""
                    },
                    model_info = new { }
                };

                var body = JsonSerializer.SerializeToUtf8Bytes(showResponse, JsonOptions);
                return new TunnelMessage
                {
                    Type = TunnelMessageTypes.ModelCommandResult,
                    RequestId = message.RequestId,
                    StatusCode = 200,
                    ReasonPhrase = "OK",
                    Body = body
                };
            }

            default:
                throw new InvalidOperationException($"Unsupported model command '{message.Command}' with llama.cpp.");
        }
    }

    private static TunnelMessage BuildModelCommandResult(
        string requestId,
        HttpResponseMessage response,
        byte[] body)
    {
        return new TunnelMessage
        {
            Type = TunnelMessageTypes.ModelCommandResult,
            RequestId = requestId,
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Body = body
        };
    }

    private static TunnelMessage BuildModelCommandResult(
        string requestId, int statusCode, string reasonPhrase, byte[] body)
    {
        return new TunnelMessage
        {
            Type = TunnelMessageTypes.ModelCommandResult,
            RequestId = requestId,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Body = body
        };
    }

    private static bool ShouldRetryModelCommandWithEmbedding(
        string? command,
        HttpResponseMessage response,
        byte[] body)
    {
        var normalizedCommand = NormalizeModelCommand(command);
        if (normalizedCommand is not ("load" or "unload")
            || response.IsSuccessStatusCode)
        {
            return false;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return true;
        }

        var responseText = body.Length > 0
            ? Encoding.UTF8.GetString(body)
            : "";

        return responseText.Contains("does not support generate", StringComparison.OrdinalIgnoreCase);
    }

    private HttpRequestMessage BuildModelCommandRequest(TunnelMessage message)
    {
        return BuildModelCommandRequest(_options.Upstream, message.Command, message.Model);
    }

    internal static HttpRequestMessage BuildModelCommandRequest(Uri upstream, string? command, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new InvalidOperationException("Model command is missing a model name.");
        }

        var model = modelName.Trim();
        var normalizedCommand = NormalizeModelCommand(command);

        return normalizedCommand switch
        {
            "pull" => new HttpRequestMessage(HttpMethod.Post, new Uri(upstream, "/api/pull"))
            {
                Content = JsonContent(new { model, stream = false })
            },
            "delete" => new HttpRequestMessage(HttpMethod.Delete, new Uri(upstream, "/api/delete"))
            {
                Content = JsonContent(new { model })
            },
            "load" => new HttpRequestMessage(HttpMethod.Post, new Uri(upstream, "/api/generate"))
            {
                Content = JsonContent(new { model, stream = false, keep_alive = -1 })
            },
            "unload" => new HttpRequestMessage(HttpMethod.Post, new Uri(upstream, "/api/generate"))
            {
                Content = JsonContent(new { model, stream = false, keep_alive = 0 })
            },
            "show" => new HttpRequestMessage(HttpMethod.Post, new Uri(upstream, "/api/show"))
            {
                Content = JsonContent(new { model })
            },
            _ => throw new InvalidOperationException($"Unsupported model command '{command}'.")
        };
    }

    internal static HttpRequestMessage BuildEmbeddingModelCommandRequest(Uri upstream, string? command, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new InvalidOperationException("Model command is missing a model name.");
        }

        var model = modelName.Trim();
        var normalizedCommand = NormalizeModelCommand(command);

        return normalizedCommand switch
        {
            "load" => new HttpRequestMessage(HttpMethod.Post, new Uri(upstream, "/api/embed"))
            {
                Content = JsonContent(new { model, input = EmbeddingWarmupInput, keep_alive = -1 })
            },
            "unload" => new HttpRequestMessage(HttpMethod.Post, new Uri(upstream, "/api/embed"))
            {
                Content = JsonContent(new { model, input = EmbeddingWarmupInput, keep_alive = 0 })
            },
            _ => throw new InvalidOperationException($"Unsupported embedding model command '{command}'.")
        };
    }

    private static string NormalizeModelCommand(string? command) =>
        (command ?? "").Trim().ToLowerInvariant();

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private void StartRequest(ClientWebSocket socket, TunnelMessage message, CancellationToken cancellationToken)
    {
        Uri? effectiveUpstream = null;

        if (_llamaCppManager is not null)
        {
            var modelName = UpstreamRequest.ExtractModelName(message);
            if (modelName is not null)
            {
                effectiveUpstream = _llamaCppManager.GetUpstream(modelName);
                if (effectiveUpstream is null)
                {
                    _logger.LogWarning(
                        "Request for model '{Model}' but no llama.cpp container is running for it. Falling back to default upstream.",
                        modelName);
                }
            }
        }

        var request = new UpstreamRequest(
            _options,
            _httpClient,
            message,
            (response, token) => SendAsync(socket, response, token),
            requestId => _activeRequests.TryRemove(requestId, out _),
            cancellationToken,
            effectiveUpstream: effectiveUpstream);

        if (!_activeRequests.TryAdd(message.RequestId, request))
        {
            _ = SendAsync(
                socket,
                new TunnelMessage
                {
                    Type = TunnelMessageTypes.Error,
                    RequestId = message.RequestId,
                    Error = "Duplicate request id."
                },
                cancellationToken);
            return;
        }

        _ = Task.Run(request.RunAsync, cancellationToken);
    }

    private async Task SendAsync(ClientWebSocket socket, TunnelMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await WebSocketMessageTransport.SendAsync(socket, message, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void CancelAllActiveRequests()
    {
        foreach (var pair in _activeRequests.ToArray())
        {
            if (_activeRequests.TryRemove(pair.Key, out var request))
            {
                request.Cancel();
            }
        }
    }
}
