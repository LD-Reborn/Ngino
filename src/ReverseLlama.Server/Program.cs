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

if (settings.Keycloak.IsConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.UseElmah();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.MapAdminEndpoints(settings);

app.MapGet("/", (TunnelHub hub) =>
    Results.Json(new
    {
        status = "ok",
        connected = hub.HasClient,
        pendingRequests = hub.PendingRequestCount,
        clients = hub.ClientsSnapshot.Count
    }));

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
