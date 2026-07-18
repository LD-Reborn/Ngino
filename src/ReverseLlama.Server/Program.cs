using System.Data.SqlClient;
using System.Net.WebSockets;
using ElmahCore;
using ElmahCore.Mvc;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ReverseLlama.Protocol;
using ReverseLlama.Server;

var builder = WebApplication.CreateBuilder(args);
var settings = ServerSettings.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<TunnelHub>();
builder.Services.AddSingleton<EmbeddingCache>();
builder.Services.AddSingleton<ManagementStore>();
builder.Services.AddSingleton<AuthRateLimiter>();
builder.Services.AddElmah<ElmahCore.MySql.MySqlErrorLog>().Configure<ElmahOptions>(
    options => options.ConnectionString = builder.Configuration.GetConnectionString("ElmahConnection"));

if (settings.Keycloak.IsConfigured)
{
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "ReverseLlama.Admin";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/admin/login";
            options.LogoutPath = "/admin/logout";
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = settings.Keycloak.Authority;
            options.ClientId = settings.Keycloak.ClientId;
            options.ClientSecret = settings.Keycloak.ClientSecret;
            options.RequireHttpsMetadata = settings.Keycloak.RequireHttpsMetadata;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.Events = new OpenIdConnectEvents
            {
                OnRemoteFailure = context =>
                {
                    var errorLog = context.HttpContext.RequestServices.GetService<ErrorLog>();
                    if (context.Failure is not null)
                    {
                        errorLog?.Log(new Error(context.Failure));
                    }

                    context.HandleResponse();
                    context.Response.Redirect("/admin/auth-error");
                    return Task.CompletedTask;
                }
            };
        });
}

builder.Services.AddAuthorization();

var app = builder.Build();

var managementStore = app.Services.GetRequiredService<ManagementStore>();
var tunnelHub = app.Services.GetRequiredService<TunnelHub>();
managementStore.SetConnectedClientProvider(() => tunnelHub.ClientSnapshots.Select(c => c.Id));

if (settings.Keycloak.IsConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.UseElmah();

app.Use(async (context, next) =>
{
    context.Response.Headers.AccessControlAllowOrigin = "*";
    context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, PATCH, OPTIONS";
    context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Authorization";

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

var rateLimiter = app.Services.GetRequiredService<AuthRateLimiter>();

app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method))
    {
        await next();
        return;
    }

    var ip = AuthRateLimiter.GetClientIp(context.Request);
    var (allowed, retryAfter, _) = rateLimiter.CheckRateLimit(ip);

    if (!allowed)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = ((int)retryAfter!.Value.TotalSeconds).ToString();
        context.Response.ContentType = "application/json";
        var seconds = (int)retryAfter!.Value.TotalSeconds;
        string retryMessage;
        if (seconds >= 3600)
        {
            var hours = seconds / 3600;
            retryMessage = $"Please try again in {hours} hour{(hours == 1 ? "" : "s")}.";
        }
        else if (seconds >= 60)
        {
            var minutes = seconds / 60;
            retryMessage = $"Please try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
        }
        else
        {
            retryMessage = $"Please try again in {seconds} second{(seconds == 1 ? "" : "s")}.";
        }
        await context.Response.WriteAsJsonAsync(new { error = $"Too many requests. {retryMessage}" }, context.RequestAborted);
        return;
    }

    context.Response.OnStarting(() =>
    {
        if (context.Response.StatusCode is StatusCodes.Status401Unauthorized)
        {
            rateLimiter.RecordFailure(ip, context.Request.Path);
        }
        else if (context.Response.StatusCode is StatusCodes.Status302Found
            && context.Request.Path.StartsWithSegments("/api/admin"))
        {
            var location = context.Response.Headers.Location.FirstOrDefault();
            if (location is not null
                && location.Contains("/admin/login", StringComparison.OrdinalIgnoreCase))
            {
                rateLimiter.RecordFailure(ip, context.Request.Path);
            }
        }

        return Task.CompletedTask;
    });

    await next();
});

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.MapAdminEndpoints(settings);

app.MapGet("/", () => Results.Redirect("/admin"));

app.MapGet(settings.StatusPath, (HttpContext context, TunnelHub hub, ServerSettings serverSettings, EmbeddingCache embeddingCache, ManagementStore managementStore) =>
{
    // Query token allowed so the status page can be checked in a browser.
    if (!TokenAuthentication.IsAuthorized(context.Request, serverSettings, managementStore, allowQueryToken: true))
    {
        return Results.Unauthorized();
    }

    return Results.Json(new
    {
        connected = hub.HasClient,
        pendingRequests = hub.PendingRequestCount,
        tunnelPath = serverSettings.TunnelPath,
        embeddingCache = new
        {
            available = embeddingCache.IsAvailable,
            count = embeddingCache.Count,
            databasePath = embeddingCache.DatabasePath,
            lastError = embeddingCache.LastError
        },
        management = new
        {
            available = managementStore.IsAvailable,
            databasePath = managementStore.DatabasePath,
            lastError = managementStore.LastError
        },
        clients = hub.ClientsSnapshot
    });
});

app.Map(settings.TunnelPath, async (HttpContext context, TunnelHub hub, ServerSettings serverSettings, ManagementStore managementStore) =>
{
    if (!TokenAuthentication.IsAuthorized(context.Request, serverSettings, managementStore, allowQueryToken: true))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync($"Missing or invalid {ProtocolConstants.TokenHeader}.", context.RequestAborted);
        return;
    }

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("This endpoint only accepts WebSocket tunnel connections.", context.RequestAborted);
        return;
    }

    var clientId = context.Request.Headers[ProtocolConstants.ClientIdHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(clientId))
    {
        clientId = $"anonymous-{Guid.NewGuid():n}";
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.AcceptAsync(clientId, socket, context.RequestAborted);
});

app.Map("/clients/{clientId}/{**path}", ReverseProxyEndpoint.HandleClientAsync);

app.Map("/{**path}", ReverseProxyEndpoint.HandleRootAsync)
    .WithOrder(1000);

var elmahService = app.Services.GetRequiredService<ErrorLog>();
try
{
    app.Run();
}
catch (Exception exception)
{
    elmahService.Log(new Error(exception));
    throw;
}
