using System.Text.Json;
using ElmahCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Ngino.Protocol;

namespace Ngino.Server;

internal static class ReverseProxyEndpoint
{
    private const string UnauthorizedMessage = "Missing or invalid Ngino token.";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Expect",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    private static readonly HashSet<string> InternalHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ProtocolConstants.TokenHeader
    };

    public static async Task HandleRootAsync(
        HttpContext context,
        TunnelHub hub,
        ServerSettings settings,
        ILoggerFactory loggerFactory,
        EmbeddingCache embeddingCache,
        ManagementStore managementStore)
    {
        var auth = TokenAuthentication.Authorize(context.Request, settings, managementStore, allowQueryToken: false, allowPathToken: true);
        if (!auth.IsAuthorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(UnauthorizedMessage, context.RequestAborted);
            return;
        }

        var billingCheck = managementStore.CheckBalanceForUserKey(auth.UserKeyId);
        if (!billingCheck.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Insufficient balance.",
                balance = billingCheck.Balance,
                currency = billingCheck.Currency,
                threshold = billingCheck.Threshold
            }, context.RequestAborted);
            return;
        }

        var groupAccess = ResolveGroupAccess(auth.UserKeyId, managementStore);

        var pathTokenRemoved = TokenAuthentication.TryRemovePathToken(context.Request.Path, settings, managementStore, out var proxyPath);
        if (!pathTokenRemoved)
        {
            proxyPath = context.Request.Path;
        }

        if (pathTokenRemoved && HttpMethods.IsGet(context.Request.Method) && IsRootPath(proxyPath))
        {
            await WriteRootStatusAsync(context, hub);
            return;
        }

        if (TryGetClientAddress(proxyPath, out var pathClientId, out var clientPath))
        {
            if (!groupAccess.IsClientAllowed(pathClientId))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync($"Access to client '{pathClientId}' is not permitted.", context.RequestAborted);
                return;
            }

            await ForwardToClientAsync(
                context,
                pathClientId,
                clientPath,
                $"{clientPath}{context.Request.QueryString}",
                hub,
                settings,
                loggerFactory,
                embeddingCache,
                managementStore,
                groupAccess,
                auth.UserKeyId);
            return;
        }

        if (IsTagsRequest(context.Request, proxyPath))
        {
            await HandleTagsAsync(context, hub, managementStore, groupAccess);
            return;
        }

        var embeddingRequest = await embeddingCache.TryReadRequestAsync(context.Request, proxyPath);
        if (embeddingRequest is not null
            && await embeddingCache.TryWriteCachedResponseAsync(context, embeddingRequest))
        {
            return;
        }

        var requestedModel = embeddingRequest?.Model ?? await GetRequestedModelAsync(context.Request, proxyPath);
        var connection = hub.SelectBest(
            requestedModel,
            clientId => !managementStore.GetClientAccess(clientId).IsDisabled
                && (requestedModel is null
                    ? groupAccess.IsClientAllowed(clientId)
                    : groupAccess.IsClientModelAllowed(clientId, requestedModel)));
        if (connection is null)
        {
            if (!hub.HasClient)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("No tunnel client is connected.", context.RequestAborted);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(GetNoRouteMessage(requestedModel), context.RequestAborted);
            return;
        }

        var pathAndQuery = $"{proxyPath}{context.Request.QueryString}";
        await ForwardAsync(
            context,
            connection,
            pathAndQuery,
            requestedModel,
            settings,
            loggerFactory,
            embeddingCache,
            embeddingRequest,
            managementStore,
            auth.UserKeyId);
    }

    public static async Task HandleClientAsync(
        HttpContext context,
        string clientId,
        string? path,
        TunnelHub hub,
        ServerSettings settings,
        ILoggerFactory loggerFactory,
        EmbeddingCache embeddingCache,
        ManagementStore managementStore)
    {
        var auth = TokenAuthentication.Authorize(context.Request, settings, managementStore, allowQueryToken: false, allowPathToken: true);
        if (!auth.IsAuthorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(UnauthorizedMessage, context.RequestAborted);
            return;
        }

        var billingCheck = managementStore.CheckBalanceForUserKey(auth.UserKeyId);
        if (!billingCheck.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Insufficient balance.",
                balance = billingCheck.Balance,
                currency = billingCheck.Currency,
                threshold = billingCheck.Threshold
            }, context.RequestAborted);
            return;
        }

        var groupAccess = ResolveGroupAccess(auth.UserKeyId, managementStore);
        if (!groupAccess.IsClientAllowed(clientId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync($"Access to client '{clientId}' is not permitted.", context.RequestAborted);
            return;
        }

        var pathAndQuery = $"/{path}{context.Request.QueryString}";
        var clientPath = new PathString($"/{path}");
        await ForwardToClientAsync(
            context,
            clientId,
            clientPath,
            pathAndQuery,
            hub,
            settings,
            loggerFactory,
            embeddingCache,
            managementStore,
            groupAccess,
            auth.UserKeyId);
    }

    private static async Task ForwardToClientAsync(
        HttpContext context,
        string clientId,
        PathString clientPath,
        string pathAndQuery,
        TunnelHub hub,
        ServerSettings settings,
        ILoggerFactory loggerFactory,
        EmbeddingCache embeddingCache,
        ManagementStore managementStore,
        GroupAccess? groupAccess = null,
        string? userKeyId = null)
    {
        var clientAccess = managementStore.GetClientAccess(clientId);
        if (clientAccess.IsDisabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(GetClientDisabledMessage(clientId, clientAccess), context.RequestAborted);
            return;
        }

        var embeddingRequest = await embeddingCache.TryReadRequestAsync(context.Request, clientPath);
        if (embeddingRequest is not null
            && await embeddingCache.TryWriteCachedResponseAsync(context, embeddingRequest))
        {
            return;
        }

        var connection = hub.Get(clientId);
        if (connection is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"No tunnel client with id '{clientId}' is connected.", context.RequestAborted);
            return;
        }

        var requestedModel = embeddingRequest?.Model ?? await GetRequestedModelAsync(context.Request, clientPath);
        if (requestedModel is not null && groupAccess is not null && !groupAccess.IsClientModelAllowed(clientId, requestedModel))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync($"Access to model '{requestedModel}' on client '{clientId}' is not permitted.", context.RequestAborted);
            return;
        }

        await ForwardAsync(
            context,
            connection,
            pathAndQuery,
            requestedModel,
            settings,
            loggerFactory,
            embeddingCache,
            embeddingRequest,
            managementStore,
            userKeyId);
    }

    private static bool IsRootPath(PathString path) =>
        string.IsNullOrEmpty(path.Value) || path.Value.Equals("/", StringComparison.Ordinal);

    private static Task WriteRootStatusAsync(HttpContext context, TunnelHub hub) =>
        context.Response.WriteAsJsonAsync(
            new
            {
                status = "ok",
                connected = hub.HasClient,
                pendingRequests = hub.PendingRequestCount,
                clients = hub.ClientsSnapshot.Count
            },
            context.RequestAborted);

    private static bool TryGetClientAddress(PathString path, out string clientId, out PathString clientPath)
    {
        clientId = "";
        clientPath = PathString.Empty;

        if (!path.StartsWithSegments(new PathString("/clients"), out var pathAfterPrefix))
        {
            return false;
        }

        var value = pathAfterPrefix.Value ?? "";
        if (value.Length <= 1 || value[0] != '/')
        {
            return false;
        }

        var nextSlash = value.IndexOf('/', 1);
        clientId = nextSlash < 0
            ? value[1..]
            : value[1..nextSlash];

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        clientPath = nextSlash < 0
            ? new PathString("/")
            : new PathString(value[nextSlash..]);
        return true;
    }

    private static GroupAccess ResolveGroupAccess(string? userKeyId, ManagementStore managementStore)
    {
        if (string.IsNullOrWhiteSpace(userKeyId))
        {
            return GroupAccess.Unrestricted;
        }

        return managementStore.ResolveGroupAccess(userKeyId);
    }

    private static bool IsTagsRequest(HttpRequest request, PathString proxyPath) =>
        HttpMethods.IsGet(request.Method)
        && string.Equals(proxyPath.Value, "/api/tags", StringComparison.OrdinalIgnoreCase);

    private static async Task HandleTagsAsync(
        HttpContext context,
        TunnelHub hub,
        ManagementStore managementStore,
        GroupAccess groupAccess)
    {
        var models = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var client in hub.ClientSnapshots)
        {
            foreach (var model in client.Models)
            {
                if (!models.TryGetValue(model, out var clients))
                {
                    clients = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    models[model] = clients;
                }

                clients.Add(client.Id);
            }
        }

        var filteredModels = new List<object>();
        foreach (var (model, clients) in models.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var accessibleClients = clients
                .Where(clientId => groupAccess.IsClientModelAllowed(clientId, model))
                .ToList();

            if (accessibleClients.Count > 0)
            {
                filteredModels.Add(new
                {
                    name = model,
                    model,
                    modified_at = DateTimeOffset.UtcNow,
                    size = 0,
                    digest = "",
                    details = new { }
                });
            }
        }

        var response = new { models = filteredModels };
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(response, context.RequestAborted);
    }

    private static string GetNoRouteMessage(string? requestedModel) =>
        string.IsNullOrWhiteSpace(requestedModel)
            ? "No tunnel client is available for this request."
            : $"No connected tunnel client reports model '{requestedModel}'. Check the status endpoint for connected client model lists.";

    private static string GetClientDisabledMessage(string clientId, ClientAccess access)
    {
        var reason = string.IsNullOrWhiteSpace(access.DisabledReason)
            ? ""
            : $" Reason: {access.DisabledReason.Trim()}.";

        if (access.DisabledManually)
        {
            return $"Tunnel client '{clientId}' is disabled until it is enabled manually.{reason}";
        }

        if (access.DisabledUntilUtc is { } disabledUntil)
        {
            var from = access.DisabledFromUtc is { } fromUtc
                ? $" (scheduled from {fromUtc:O})"
                : "";
            return $"Tunnel client '{clientId}' is disabled until {disabledUntil:O}.{from}{reason}";
        }

        return access.DisabledFromUtc is { } fromUtc2
            ? $"Tunnel client '{clientId}' is disabled (scheduled from {fromUtc2:O}).{reason}"
            : $"Tunnel client '{clientId}' is disabled.{reason}";
    }

    private static async Task<string?> GetRequestedModelAsync(HttpRequest request, PathString proxyPath)
    {
        if (TryGetModelFromPath(proxyPath, out var pathModel))
        {
            return pathModel;
        }

        if (request.Query.TryGetValue("model", out var queryValues))
        {
            var queryModel = queryValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(queryModel))
            {
                return queryModel;
            }
        }

        if (!CanHaveBody(request) || (!IsJsonRequest(request) && !IsLikelyModelRequestPath(proxyPath)))
        {
            return null;
        }

        request.EnableBuffering();

        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: request.HttpContext.RequestAborted);

            return TryGetModelFromJson(document.RootElement, out var bodyModel)
                ? bodyModel
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static bool TryGetModelFromPath(PathString path, out string model)
    {
        model = "";

        const string openAiModelPrefix = "/v1/models/";
        var value = path.Value ?? "";
        if (!value.StartsWith(openAiModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remaining = value[openAiModelPrefix.Length..];
        var nextSlash = remaining.IndexOf('/');
        model = Uri.UnescapeDataString(nextSlash < 0 ? remaining : remaining[..nextSlash]);

        return !string.IsNullOrWhiteSpace(model);
    }

    private static bool TryGetModelFromJson(JsonElement root, out string model)
    {
        model = "";

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("model", out var modelElement)
            || modelElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        model = modelElement.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(model);
    }

    private static bool IsJsonRequest(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContentType))
        {
            return false;
        }

        var mediaType = request.ContentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyModelRequestPath(PathString path)
    {
        var value = path.Value ?? "";

        return value.Equals("/api/generate", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/api/chat", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/api/embed", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/api/embeddings", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/api/show", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/v1/completions", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/v1/embeddings", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ForwardAsync(
        HttpContext context,
        TunnelConnection connection,
        string pathAndQuery,
        string? requestedModel,
        ServerSettings settings,
        ILoggerFactory loggerFactory,
        EmbeddingCache embeddingCache,
        EmbeddingCacheRequest? embeddingRequest,
        ManagementStore managementStore,
        string? userKeyId = null)
    {
        var logger = loggerFactory.CreateLogger("Ngino.Server.ReverseProxy");
        var requestId = Guid.NewGuid().ToString("n");
        var pending = connection.RegisterPending(requestId);
        var startedAt = DateTimeOffset.UtcNow;
        var tokenCounter = new ResponseTokenCounter();
        int? statusCode = null;
        var responseCompleted = false;
        Task? requestBodyTask = null;

        try
        {
            var hasBody = CanHaveBody(context.Request);
            var requestMessage = new TunnelMessage
            {
                Type = TunnelMessageTypes.HttpRequest,
                RequestId = requestId,
                Method = context.Request.Method,
                PathAndQuery = pathAndQuery,
                HasBody = hasBody,
                Headers = CollectRequestHeaders(context.Request, settings, managementStore)
            };

            await connection.SendAsync(requestMessage, context.RequestAborted);
            requestBodyTask = ForwardRequestBodyAsync(context.Request, connection, requestId, hasBody, settings, logger);
            _ = requestBodyTask.ContinueWith(
                task => pending.Fail(task.Exception!.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            var responseHeaders = await pending.WaitForHeadersAsync(context.RequestAborted);
            statusCode = responseHeaders.StatusCode;
            ApplyResponseHeaders(context.Response, responseHeaders);

            await context.Response.StartAsync(context.RequestAborted);

            if (embeddingRequest is not null)
            {
                var body = await ReadResponseBodyAsync(pending, context.RequestAborted);
                tokenCounter.Add(body);
                await context.Response.Body.WriteAsync(body, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);

                await embeddingCache.StoreResponseAsync(
                    embeddingRequest,
                    responseHeaders,
                    body,
                    CancellationToken.None);
            }
            else
            {
                await foreach (var chunk in pending.Body.ReadAllAsync(context.RequestAborted))
                {
                    tokenCounter.Add(chunk);
                    await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }

            responseCompleted = true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Proxy request {RequestId} was cancelled by the downstream caller.", requestId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Proxy request {RequestId} failed.", requestId);

            var errorLog = context.RequestServices.GetService<ErrorLog>();
            if (errorLog is not null)
            {
                await errorLog.LogAsync(new Error(exception, context));
            }

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Bad gateway", CancellationToken.None);
            }
            else
            {
                context.Abort();
            }
        }
        finally
        {
            connection.RemovePending(requestId);
            var completedAt = DateTimeOffset.UtcNow;
            var tokenCounts = tokenCounter.CountTokens();

            var cost = 0.0;
            if (!string.IsNullOrWhiteSpace(userKeyId) && tokenCounts.TotalTokens > 0)
            {
                var billing = managementStore.ResolveBillingForUserKey(userKeyId);
                if (billing is not null)
                {
                    cost = managementStore.CalculateCost(billing.GroupId, requestedModel, tokenCounts.TotalTokens);
                }
            }

            managementStore.RecordRequest(new RequestMetric(
                connection.ClientId,
                requestedModel,
                context.Request.Method,
                pathAndQuery,
                statusCode ?? (context.Response.HasStarted ? context.Response.StatusCode : null),
                tokenCounts.PromptTokens,
                tokenCounts.CompletionTokens,
                tokenCounts.TotalTokens,
                userKeyId,
                cost,
                startedAt,
                completedAt,
                completedAt - startedAt));

            if (!responseCompleted && connection.IsOpen)
            {
                try
                {
                    await connection.SendAsync(
                        new TunnelMessage
                        {
                            Type = TunnelMessageTypes.Cancel,
                            RequestId = requestId
                        },
                        CancellationToken.None);
                }
                catch
                {
                    // The tunnel is already gone; nothing useful remains to notify.
                }
            }

            if (requestBodyTask is { IsCompleted: true })
            {
                try
                {
                    await requestBodyTask;
                }
                catch
                {
                    // Already reflected through the proxy response path above.
                }
            }
        }
    }

    private static async Task<byte[]> ReadResponseBodyAsync(PendingProxyRequest pending, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();

        await foreach (var chunk in pending.Body.ReadAllAsync(cancellationToken))
        {
            await memory.WriteAsync(chunk, cancellationToken);
        }

        return memory.ToArray();
    }

    private static async Task ForwardRequestBodyAsync(
        HttpRequest request,
        TunnelConnection connection,
        string requestId,
        bool hasBody,
        ServerSettings settings,
        ILogger logger)
    {
        try
        {
            if (hasBody)
            {
                var buffer = new byte[settings.ChunkSize];

                while (true)
                {
                    var bytesRead = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    await connection.SendAsync(
                        new TunnelMessage
                        {
                            Type = TunnelMessageTypes.HttpRequestBody,
                            RequestId = requestId,
                            Body = buffer.AsSpan(0, bytesRead).ToArray()
                        },
                        request.HttpContext.RequestAborted);
                }
            }

            await connection.SendAsync(
                new TunnelMessage
                {
                    Type = TunnelMessageTypes.HttpRequestComplete,
                    RequestId = requestId
                },
                request.HttpContext.RequestAborted);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed while forwarding request body {RequestId}.", requestId);
            throw;
        }
    }

    private static bool CanHaveBody(HttpRequest request)
    {
        var bodyDetection = request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>();
        if (bodyDetection?.CanHaveBody is bool canHaveBody)
        {
            return canHaveBody;
        }

        return request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding");
    }

    private static List<HeaderPair> CollectRequestHeaders(
        HttpRequest request,
        ServerSettings settings,
        ManagementStore managementStore)
    {
        var headers = new List<HeaderPair>();
        var skip = HeadersToSkip(request.Headers);

        foreach (var header in request.Headers)
        {
            if (skip.Contains(header.Key) || InternalHeaders.Contains(header.Key))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (IsOwnBearerToken(header.Key, value, settings, managementStore))
                {
                    continue;
                }

                headers.Add(new HeaderPair(header.Key, value ?? ""));
            }
        }

        return headers;
    }

    // Our token in Bearer form authenticates against the proxy and must not
    // leak upstream; any other Authorization header is forwarded untouched.
    private static bool IsOwnBearerToken(
        string headerName,
        string? value,
        ServerSettings settings,
        ManagementStore managementStore) =>
        string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase)
        && TokenAuthentication.IsOwnBearerValue(value, settings, managementStore);

    private static void ApplyResponseHeaders(HttpResponse response, TunnelMessage responseHeaders)
    {
        response.StatusCode = responseHeaders.StatusCode ?? StatusCodes.Status502BadGateway;

        foreach (var group in responseHeaders.Headers.GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ShouldSkipResponseHeader(group.Key))
            {
                continue;
            }

            response.Headers[group.Key] = new StringValues(group.Select(header => header.Value).ToArray());
        }
    }

    private static HashSet<string> HeadersToSkip(IHeaderDictionary headers)
    {
        var skip = new HashSet<string>(HopByHopHeaders, StringComparer.OrdinalIgnoreCase);

        if (headers.TryGetValue("Connection", out var connectionHeader))
        {
            foreach (var value in connectionHeader)
            {
                foreach (var headerName in value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                {
                    skip.Add(headerName);
                }
            }
        }

        return skip;
    }

    private static bool ShouldSkipResponseHeader(string headerName) =>
        HopByHopHeaders.Contains(headerName);
}
