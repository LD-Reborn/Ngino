using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.StaticFiles;

namespace ReverseLlama.Server;

internal static class AdminEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapAdminEndpoints(this WebApplication app, ServerSettings settings)
    {
        if (settings.Keycloak.IsConfigured)
        {
            app.MapGet("/admin/login", (string? returnUrl) =>
                Results.Challenge(
                    new AuthenticationProperties { RedirectUri = NormalizeLocalReturnUrl(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme]))
                .AllowAnonymous();

            app.MapPost("/admin/logout", () =>
                Results.SignOut(
                    new AuthenticationProperties { RedirectUri = "/admin" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
                .RequireAuthorization();
        }

        app.MapGet("/admin/auth-error", () =>
            Results.Text(
                "Login failed while processing the Keycloak callback. The exception was written to ELMAH.",
                "text/plain"))
            .AllowAnonymous();

        var api = app.MapGroup("/api/admin");

        if (settings.Keycloak.IsConfigured)
        {
            api.RequireAuthorization();
        }

        api.MapGet("/summary", (HttpContext context, TunnelHub hub, ManagementStore store) =>
            Results.Json(BuildSummary(context.User, hub, store, settings)));

        api.MapGet("/me", (HttpContext context, ManagementStore store) =>
            Results.Json(new
            {
                authenticated = context.User.Identity?.IsAuthenticated ?? false,
                name = GetUserName(context.User),
                keycloakConfigured = settings.Keycloak.IsConfigured,
                management = new
                {
                    available = store.IsAvailable,
                    databasePath = store.DatabasePath,
                    lastError = store.LastError
                }
            }));

        api.MapPost("/clients/{clientId}/disable", (string clientId, DisableClientRequest request, ManagementStore store) =>
        {
            try
            {
                var manual = string.Equals(request.Mode, "manual", StringComparison.OrdinalIgnoreCase);
                TimeSpan? duration = manual
                    ? null
                    : TimeSpan.FromMinutes(Math.Clamp(request.DurationMinutes ?? 60, 1, 24 * 60));

                store.DisableClient(clientId, duration, manual, request.Reason);
                return Results.Ok(new { clientId, disabled = true });
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapPost("/clients/{clientId}/enable", (string clientId, ManagementStore store) =>
        {
            try
            {
                store.EnableClient(clientId);
                return Results.Ok(new { clientId, disabled = false });
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapGet("/models/detail", async (
            HttpContext context,
            string model,
            string? clientId,
            TunnelHub hub,
            ManagementStore store) =>
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return Results.BadRequest(new { error = "Model is required." });
            }

            var modelSummary = BuildModelSummaries(hub, store)
                .FirstOrDefault(item => item.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
            var selectedClientId = ResolveModelClientId(hub, modelSummary, model, clientId);
            object? show = null;

            if (!string.IsNullOrWhiteSpace(selectedClientId))
            {
                var connection = hub.Get(selectedClientId);
                if (connection is not null)
                {
                    show = await SendModelCommandForApiAsync(
                        connection,
                        "show",
                        model,
                        TimeSpan.FromSeconds(60),
                        context.RequestAborted);
                }
            }

            return Results.Json(new
            {
                model,
                listedClients = modelSummary?.ListedClients ?? [],
                activeClients = modelSummary?.ActiveClients ?? [],
                metrics = modelSummary?.Metrics ?? EmptyModelMetrics(),
                selectedClientId,
                show
            });
        });

        api.MapPost("/models/actions", async (
            HttpContext context,
            ModelActionRequest request,
            TunnelHub hub) =>
        {
            if (string.IsNullOrWhiteSpace(request.ClientId)
                || string.IsNullOrWhiteSpace(request.Model)
                || string.IsNullOrWhiteSpace(request.Action))
            {
                return Results.BadRequest(new { error = "Client id, model, and action are required." });
            }

            if (!TryMapModelAction(request.Action, out var command, out var timeout))
            {
                return Results.BadRequest(new { error = $"Unsupported action '{request.Action}'." });
            }

            var connection = hub.Get(request.ClientId);
            if (connection is null)
            {
                return Results.NotFound(new { error = $"Client '{request.ClientId}' is not connected." });
            }

            var result = await SendModelCommandForApiAsync(
                connection,
                command,
                request.Model,
                timeout,
                context.RequestAborted);

            return Results.Json(result);
        });

        api.MapGet("/api-keys", (ManagementStore store) =>
            Results.Json(store.ListApiKeys()));

        api.MapPost("/api-keys", (CreateApiKeyRequest request, ManagementStore store) =>
        {
            try
            {
                return Results.Json(store.CreateApiKey(request.Name));
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapDelete("/api-keys/{id}", (string id, ManagementStore store) =>
            store.DeleteApiKey(id)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"API key '{id}' was not found." }));

        api.MapGet("/groups", (ManagementStore store) =>
            Results.Json(store.ListGroups()));

        api.MapPost("/groups", (CreateGroupRequest request, ManagementStore store) =>
        {
            try
            {
                return Results.Json(store.CreateGroup(request.Name));
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapGet("/groups/{id}", (string id, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            return group is not null
                ? Results.Json(group)
                : Results.NotFound(new { error = $"Group '{id}' was not found." });
        });

        api.MapPut("/groups/{id}", (string id, UpdateGroupRequest request, ManagementStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "Name is required." });
            }

            return store.UpdateGroup(id, request.Name)
                ? Results.Ok(store.GetGroup(id))
                : Results.NotFound(new { error = $"Group '{id}' was not found." });
        });

        api.MapDelete("/groups/{id}", (string id, ManagementStore store) =>
            store.DeleteGroup(id)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Group '{id}' was not found." }));

        api.MapGet("/groups/{id}/clients", (string id, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            return Results.Json(store.ListGroupClients(id));
        });

        api.MapPost("/groups/{id}/clients", (string id, AddGroupClientRequest request, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            try
            {
                var member = store.AddGroupClient(id, request.ClientId, request.Model, request.ClientPattern);
                return Results.Json(member);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = $"Failed to add member: {exception.Message}" });
            }
        });

        api.MapDelete("/groups/{groupId}/clients/{clientId:long}", (string groupId, long clientId, ManagementStore store) =>
        {
            var group = store.GetGroup(groupId);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{groupId}' was not found." });
            }

            return store.RemoveGroupClient(clientId)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Client '{clientId}' was not found." });
        });

        api.MapGet("/api-keys/groups", (ManagementStore store) =>
            Results.Json(store.ListApiKeyGroups()));

        api.MapPut("/api-keys/{id}/groups", (string id, SetApiKeyGroupsRequest request, ManagementStore store) =>
        {
            var keys = store.ListApiKeys();
            if (!keys.Any(k => k.Id == id))
            {
                return Results.NotFound(new { error = $"API key '{id}' was not found." });
            }

            try
            {
                store.SetApiKeyGroups(id, request.GroupIds ?? []);
                return Results.Ok(new { apiKeyId = id, groupIds = store.GetApiKeyGroupIds(id) });
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        var adminHome = app.MapGet("/admin", (IWebHostEnvironment environment) =>
            ServeAdminAsset(environment, null));
        var adminAssets = app.MapGet("/admin/{**assetPath}", (IWebHostEnvironment environment, string? assetPath) =>
            ServeAdminAsset(environment, assetPath));

        if (settings.Keycloak.IsConfigured)
        {
            adminHome.RequireAuthorization();
            adminAssets.RequireAuthorization();
        }
    }

    private static object BuildSummary(
        ClaimsPrincipal user,
        TunnelHub hub,
        ManagementStore store,
        ServerSettings settings) =>
        new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            user = new
            {
                name = GetUserName(user),
                authenticated = user.Identity?.IsAuthenticated ?? false
            },
            auth = new
            {
                keycloakConfigured = settings.Keycloak.IsConfigured,
                sharedTokenConfigured = !string.IsNullOrWhiteSpace(settings.Token),
                apiKeysConfigured = store.HasApiKeys
            },
            management = new
            {
                available = store.IsAvailable,
                databasePath = store.DatabasePath,
                lastError = store.LastError
            },
            clients = BuildClientSummaries(hub, store),
            models = BuildModelSummaries(hub, store),
            apiKeys = store.ListApiKeys(),
            groups = store.ListGroups(),
            apiKeyGroups = store.ListApiKeyGroups(),
            clientGroups = store.ResolveClientGroups(
                hub.ClientSnapshots.Select(c => c.Id).ToList())
        };

    private static IReadOnlyList<ClientSummary> BuildClientSummaries(TunnelHub hub, ManagementStore store)
    {
        var connected = hub.ClientSnapshots.ToDictionary(client => client.Id, StringComparer.OrdinalIgnoreCase);
        var controls = store.ListClientControls();
        var stats = store.GetClientRequestStats();
        var clientIds = connected.Keys
            .Concat(controls.Keys)
            .Concat(stats.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(clientId => clientId, StringComparer.OrdinalIgnoreCase);
        var result = new List<ClientSummary>();

        foreach (var clientId in clientIds)
        {
            connected.TryGetValue(clientId, out var snapshot);
            controls.TryGetValue(clientId, out var access);
            stats.TryGetValue(clientId, out var requestStats);
            access ??= ClientAccess.Enabled;

            result.Add(new ClientSummary(
                clientId,
                snapshot is not null,
                snapshot?.PendingRequests ?? 0,
                snapshot?.Models ?? [],
                snapshot?.ActiveModels ?? [],
                snapshot?.ModelsUpdatedAt,
                access.IsDisabled,
                access.DisabledUntilUtc,
                access.DisabledManually,
                access.DisabledReason,
                requestStats ?? new ClientRequestStats(0, 0, 0)));
        }

        return result;
    }

    private static IReadOnlyList<ModelSummary> BuildModelSummaries(TunnelHub hub, ManagementStore store)
    {
        var listedClients = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var activeClients = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var client in hub.ClientSnapshots)
        {
            AddModelClients(listedClients, client.Models, client.Id);
            AddModelClients(activeClients, client.ActiveModels, client.Id);
        }

        var metrics = store.GetModelUsageStats();
        var modelNames = listedClients.Keys
            .Concat(activeClients.Keys)
            .Concat(metrics.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModelSummary>();

        foreach (var model in modelNames)
        {
            metrics.TryGetValue(model, out var modelMetrics);

            result.Add(new ModelSummary(
                model,
                listedClients.TryGetValue(model, out var listed) ? listed.ToArray() : [],
                activeClients.TryGetValue(model, out var active) ? active.ToArray() : [],
                modelMetrics ?? EmptyModelMetrics()));
        }

        return result;
    }

    private static void AddModelClients(
        Dictionary<string, SortedSet<string>> target,
        IEnumerable<string> models,
        string clientId)
    {
        foreach (var model in models)
        {
            if (!target.TryGetValue(model, out var clients))
            {
                clients = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                target[model] = clients;
            }

            clients.Add(clientId);
        }
    }

    private static string? ResolveModelClientId(
        TunnelHub hub,
        ModelSummary? modelSummary,
        string model,
        string? requestedClientId)
    {
        if (!string.IsNullOrWhiteSpace(requestedClientId)
            && hub.Get(requestedClientId) is not null)
        {
            return requestedClientId;
        }

        return modelSummary?.ActiveClients.FirstOrDefault(clientId => hub.Get(clientId) is not null)
            ?? modelSummary?.ListedClients.FirstOrDefault(clientId => hub.Get(clientId) is not null)
            ?? hub.SelectBest(model)?.ClientId;
    }

    private static ModelUsageStats EmptyModelMetrics() =>
        new(0, 0, 0, 0, 0);

    private static async Task<object> SendModelCommandForApiAsync(
        TunnelConnection connection,
        string command,
        string model,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await connection.SendModelCommandAsync(
                command,
                model,
                payloadJson: null,
                timeout,
                cancellationToken);
            var body = response.Body is { Length: > 0 }
                ? Encoding.UTF8.GetString(response.Body)
                : "";

            return new
            {
                ok = response.StatusCode is >= 200 and < 300,
                statusCode = response.StatusCode,
                reasonPhrase = response.ReasonPhrase,
                body = ParseJsonOrText(body)
            };
        }
        catch (OperationCanceledException)
        {
            return new
            {
                ok = false,
                statusCode = StatusCodes.Status504GatewayTimeout,
                reasonPhrase = "Timed out",
                body = "The model command timed out."
            };
        }
        catch (Exception exception)
        {
            return new
            {
                ok = false,
                statusCode = StatusCodes.Status502BadGateway,
                reasonPhrase = "Command failed",
                body = exception.Message
            };
        }
    }

    private static object? ParseJsonOrText(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return body.Length <= 100_000 ? body : body[..100_000];
        }
    }

    private static bool TryMapModelAction(string action, out string command, out TimeSpan timeout)
    {
        command = action.Trim().ToLowerInvariant() switch
        {
            "add" or "pull" => "pull",
            "remove" or "delete" => "delete",
            "load" => "load",
            "unload" => "unload",
            _ => ""
        };

        timeout = command == "pull" ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2);
        return command.Length > 0;
    }

    private static IResult ServeAdminAsset(IWebHostEnvironment environment, string? assetPath)
    {
        var path = string.IsNullOrWhiteSpace(assetPath) ? "index.html" : assetPath;

        if (path.Contains("..", StringComparison.Ordinal)
            || path.Contains('\\'))
        {
            return Results.BadRequest();
        }

        var file = environment.WebRootFileProvider.GetFileInfo($"admin/{path}");
        if (!file.Exists && !Path.HasExtension(path))
        {
            file = environment.WebRootFileProvider.GetFileInfo("admin/index.html");
        }

        if (!file.Exists)
        {
            return Results.NotFound();
        }

        ContentTypes.TryGetContentType(file.Name, out var contentType);
        return Results.Stream(file.CreateReadStream(), contentType ?? "application/octet-stream");
    }

    private static string NormalizeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith("/", StringComparison.Ordinal)
            || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return "/admin";
        }

        return returnUrl;
    }

    private static string? GetUserName(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst(ClaimTypes.Name)?.Value
        ?? user.Identity?.Name;
}

internal sealed record DisableClientRequest(
    string? Mode,
    int? DurationMinutes,
    string? Reason);

internal sealed record ModelActionRequest(
    string ClientId,
    string Model,
    string Action);

internal sealed record CreateApiKeyRequest(string? Name);

internal sealed record CreateGroupRequest(string? Name);

internal sealed record UpdateGroupRequest(string Name);

internal sealed record AddGroupClientRequest(
    string? ClientId,
    string? Model,
    string? ClientPattern);

internal sealed record SetApiKeyGroupsRequest(IReadOnlyList<string>? GroupIds);

internal sealed record ClientSummary(
    string Id,
    bool Connected,
    int PendingRequests,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> ActiveModels,
    DateTimeOffset? ModelsUpdatedAt,
    bool Disabled,
    DateTimeOffset? DisabledUntilUtc,
    bool DisabledManually,
    string? DisabledReason,
    ClientRequestStats RequestStats);

internal sealed record ModelSummary(
    string Name,
    IReadOnlyList<string> ListedClients,
    IReadOnlyList<string> ActiveClients,
    ModelUsageStats Metrics);
