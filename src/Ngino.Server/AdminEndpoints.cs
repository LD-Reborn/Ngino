using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Ngino.Server.Data;
using Ngino.Server.Models;

namespace Ngino.Server;

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
        else
        {
            app.MapGet("/admin/login", async (HttpContext context, SignInManager<ApplicationUser> signInManager, IAntiforgery antiforgery, string? returnUrl) =>
            {
                if (context.User.Identity?.IsAuthenticated == true)
                    return Results.Redirect(NormalizeLocalReturnUrl(returnUrl));

                if (await signInManager.UserManager.Users.AnyAsync())
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Content(LoginPage(NormalizeLocalReturnUrl(returnUrl), null, tokens.RequestToken!), "text/html");
                }

                return Results.Redirect("/admin/setup");
            }).AllowAnonymous();

            app.MapPost("/admin/login", async (HttpContext context, SignInManager<ApplicationUser> signInManager, IAntiforgery antiforgery, string? returnUrl, [FromForm] string? username, [FromForm] string? password) =>
            {
                if (await signInManager.UserManager.Users.AnyAsync() == false)
                    return Results.Redirect("/admin/setup");

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Content(LoginPage(NormalizeLocalReturnUrl(returnUrl), "Username and password are required.", tokens.RequestToken!), "text/html");
                }

                var result = await signInManager.PasswordSignInAsync(username, password, true, true);
                if (result.Succeeded)
                    return Results.Redirect(NormalizeLocalReturnUrl(returnUrl));

                if (result.IsLockedOut)
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Content(LoginPage(NormalizeLocalReturnUrl(returnUrl), "Account is locked out.", tokens.RequestToken!), "text/html");
                }

                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Content(LoginPage(NormalizeLocalReturnUrl(returnUrl), "Invalid username or password.", tokens.RequestToken!), "text/html");
                }
            }).AllowAnonymous();

            app.MapGet("/admin/setup", async (HttpContext context, SignInManager<ApplicationUser> signInManager, IAntiforgery antiforgery) =>
            {
                if (context.User.Identity?.IsAuthenticated == true)
                    return Results.Redirect("/admin");

                if (await signInManager.UserManager.Users.AnyAsync())
                    return Results.Redirect("/admin/login");

                var tokens = antiforgery.GetAndStoreTokens(context);
                return Results.Content(SetupPage(null, tokens.RequestToken!), "text/html");
            }).AllowAnonymous();

            app.MapPost("/admin/setup", async (HttpContext context, SignInManager<ApplicationUser> signInManager, IAntiforgery antiforgery, [FromForm] string? username, [FromForm] string? email, [FromForm] string? password, [FromForm] string? confirmPassword) =>
            {
                if (await signInManager.UserManager.Users.AnyAsync())
                    return Results.Redirect("/admin/login");

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Content(SetupPage("Username and password are required.", tokens.RequestToken!), "text/html");
                }

                if (password != confirmPassword)
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Content(SetupPage("Passwords do not match.", tokens.RequestToken!), "text/html");
                }

                var user = new ApplicationUser { UserName = username, Email = email };
                var result = await signInManager.UserManager.CreateAsync(user, password);
                if (result.Succeeded)
                {
                    await signInManager.SignInAsync(user, true);
                    return Results.Redirect("/admin");
                }

                var errors = string.Join(" ", result.Errors.Select(e => e.Description));
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Content(SetupPage(errors, tokens.RequestToken!), "text/html");
                }
            }).AllowAnonymous();

            app.MapPost("/admin/logout", async (SignInManager<ApplicationUser> signInManager) =>
            {
                await signInManager.SignOutAsync();
                return Results.Redirect("/admin/login");
            }).RequireAuthorization();
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
        else
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
                    : request.DurationMinutes is { } minutes
                        ? TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60))
                        : null;

                store.DisableClient(clientId, duration, manual, request.Reason, request.StartAtUtc, request.UntilUtc);
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

        api.MapGet("/user-keys", (ManagementStore store) =>
            Results.Json(store.ListUserKeys()));

        api.MapPost("/user-keys", (CreateUserKeyRequest request, ManagementStore store) =>
        {
            try
            {
                return Results.Json(store.CreateUserKey(request.Name));
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapDelete("/user-keys/{id}", (string id, ManagementStore store) =>
            store.DeleteUserKey(id)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"User key '{id}' was not found." }));

        api.MapGet("/client-keys", (ManagementStore store) =>
            Results.Json(store.ListClientKeys()));

        api.MapPost("/client-keys", (CreateUserKeyRequest request, ManagementStore store) =>
        {
            try
            {
                return Results.Json(store.CreateClientKey(request.Name));
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapDelete("/client-keys/{id}", (string id, ManagementStore store) =>
            store.DeleteClientKey(id)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Client key '{id}' was not found." }));

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
                var member = store.AddGroupClient(
                    id,
                    request.ClientId,
                    request.Model,
                    request.ClientPattern,
                    request.KeepaliveInstancesToKeepAlive,
                    request.KeepaliveMaxParallelismPerClient,
                    request.KeepaliveParallelismHeadroom);
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

        api.MapGet("/user-keys/groups", (ManagementStore store) =>
            Results.Json(store.ListUserKeyGroups()));

        api.MapPut("/user-keys/{id}/groups", (string id, SetUserKeyGroupsRequest request, ManagementStore store) =>
        {
            var keys = store.ListUserKeys();
            if (!keys.Any(k => k.Id == id))
            {
                return Results.NotFound(new { error = $"User key '{id}' was not found." });
            }

            try
            {
                store.SetUserKeyGroups(id, request.GroupIds ?? []);
                return Results.Ok(new { userKeyId = id, groupIds = store.GetUserKeyGroupIds(id) });
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapGet("/groups/{id}/billing", (string id, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            var billing = store.GetGroupBilling(id);
            return billing is not null
                ? Results.Json(billing)
                : Results.Json(new GroupBillingInfo(id, "EUR", 0, 0, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });

        api.MapPut("/groups/{id}/billing", (string id, UpdateBillingRequest request, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            try
            {
                var billing = store.UpsertGroupBilling(
                    id,
                    request.Currency ?? "EUR",
                    request.DefaultRatePer1k,
                    request.RefuseBelowBalance,
                    request.Enabled);
                return Results.Ok(billing);
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapGet("/groups/{id}/billing/rules", (string id, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            return Results.Json(store.ListGroupBillingRules(id));
        });

        api.MapPost("/groups/{id}/billing/rules", (string id, AddBillingRuleRequest request, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            try
            {
                var rule = store.AddBillingRule(id, request.ModelRegex, request.RatePer1k);
                return Results.Json(rule);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapPut("/groups/{id}/billing/rules/{ruleId:long}", (string id, long ruleId, UpdateBillingRuleRequest request, ManagementStore store) =>
        {
            try
            {
                return store.UpdateBillingRule(ruleId, request.ModelRegex, request.RatePer1k)
                    ? Results.Ok(new { id = ruleId })
                    : Results.NotFound(new { error = $"Rule '{ruleId}' was not found." });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapDelete("/groups/{id}/billing/rules/{ruleId:long}", (string id, long ruleId, ManagementStore store) =>
            store.DeleteBillingRule(ruleId)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Rule '{ruleId}' was not found." }));

        api.MapGet("/groups/{id}/billing/payments", (string id, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            return Results.Json(store.ListGroupPayments(id));
        });

        api.MapPost("/groups/{id}/billing/payments", (string id, AddPaymentRequest request, ManagementStore store, HttpContext context) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            try
            {
                var userName = GetUserName(context.User);
                var payment = store.AddPayment(id, request.Amount, request.Description, userName);
                return Results.Json(payment);
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapDelete("/groups/{id}/billing/payments/{paymentId:long}", (string id, long paymentId, ManagementStore store) =>
            store.DeletePayment(paymentId)
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Payment '{paymentId}' was not found." }));

        api.MapGet("/groups/{id}/billing/balance", (string id, ManagementStore store) =>
        {
            var group = store.GetGroup(id);
            if (group is null)
            {
                return Results.NotFound(new { error = $"Group '{id}' was not found." });
            }

            return Results.Json(store.GetGroupBalance(id));
        });

        api.MapGet("/usage/tokens", (ManagementStore store) =>
            Results.Json(new
            {
                byModel = store.GetTokenStatsByModel(),
                byClient = store.GetTokenStatsByClient(),
                byUserKey = store.GetTokenStatsByUserKey(),
                byGroup = store.GetTokenStatsByGroup()
            }));

        api.MapGet("/usage/revenue", (ManagementStore store) =>
            Results.Json(store.GetClientRevenue()));

        var adminHome = app.MapGet("/admin", (IWebHostEnvironment environment) =>
            ServeAdminAsset(environment, null));
        var adminAssets = app.MapGet("/admin/{**assetPath}", (IWebHostEnvironment environment, string? assetPath) =>
            ServeAdminAsset(environment, assetPath));

        if (settings.Keycloak.IsConfigured)
        {
            adminHome.RequireAuthorization();
            adminAssets.RequireAuthorization();
        }
        else
        {
            adminHome.RequireAuthorization();
            adminAssets.RequireAuthorization();
        }
    }

    private static string LoginPage(string returnUrl, string? error, string? antiforgeryToken)
    {
        var errorHtml = string.IsNullOrEmpty(error)
            ? ""
            : "<div class=\"error\">" + HtmlEncode(error) + "</div>";
        var loginAction = "/admin/login" + (returnUrl != "/admin" ? "?returnUrl=" + Uri.EscapeDataString(returnUrl) : "");

        return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>Ngino - Login</title>\n<style>\nbody{font-family:system-ui,sans-serif;background:#1a1a2e;color:#e0e0e0;display:flex;justify-content:center;align-items:center;min-height:100vh;margin:0}\n.card{background:#16213e;border:1px solid #0f3460;border-radius:12px;padding:2rem;width:100%;max-width:400px}\nh1{margin:0 0 1.5rem;font-size:1.5rem;text-align:center;color:#e94560}\nlabel{display:block;margin-bottom:.25rem;font-size:.875rem;color:#a0a0b0}\ninput{width:100%;padding:.5rem;border:1px solid #0f3460;border-radius:6px;background:#1a1a2e;color:#e0e0e0;font-size:1rem;margin-bottom:1rem;box-sizing:border-box}\ninput:focus{outline:none;border-color:#e94560}\nbutton{width:100%;padding:.625rem;border:none;border-radius:6px;background:#e94560;color:#fff;font-size:1rem;font-weight:600;cursor:pointer}\nbutton:hover{background:#c73650}\n.error{background:#3d1a1a;border:1px solid #e94560;border-radius:6px;padding:.5rem .75rem;margin-bottom:1rem;font-size:.875rem;color:#ff6b7a}\n</style>\n</head>\n<body>\n<div class=\"card\">\n<h1>Ngino Admin</h1>\n" + errorHtml + "\n<form method=\"post\" action=\"" + HtmlEncode(loginAction) + "\">\n<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"" + HtmlEncode(antiforgeryToken) + "\">\n<label for=\"username\">Username</label>\n<input type=\"text\" id=\"username\" name=\"username\" autocomplete=\"username\" required autofocus>\n<label for=\"password\">Password</label>\n<input type=\"password\" id=\"password\" name=\"password\" autocomplete=\"current-password\" required>\n<input type=\"hidden\" name=\"returnUrl\" value=\"" + HtmlEncode(returnUrl) + "\">\n<button type=\"submit\">Sign In</button>\n</form>\n</div>\n</body>\n</html>";
    }

    private static string SetupPage(string? error, string? antiforgeryToken)
    {
        var errorHtml = string.IsNullOrEmpty(error)
            ? ""
            : "<div class=\"error\">" + HtmlEncode(error) + "</div>";

        return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>Ngino - Initial Setup</title>\n<style>\nbody{font-family:system-ui,sans-serif;background:#1a1a2e;color:#e0e0e0;display:flex;justify-content:center;align-items:center;min-height:100vh;margin:0}\n.card{background:#16213e;border:1px solid #0f3460;border-radius:12px;padding:2rem;width:100%;max-width:400px}\nh1{margin:0 0 .25rem;font-size:1.5rem;text-align:center;color:#e94560}\n.subtitle{text-align:center;color:#a0a0b0;margin-bottom:1.5rem;font-size:.875rem}\nlabel{display:block;margin-bottom:.25rem;font-size:.875rem;color:#a0a0b0}\ninput{width:100%;padding:.5rem;border:1px solid #0f3460;border-radius:6px;background:#1a1a2e;color:#e0e0e0;font-size:1rem;margin-bottom:1rem;box-sizing:border-box}\ninput:focus{outline:none;border-color:#e94560}\nbutton{width:100%;padding:.625rem;border:none;border-radius:6px;background:#e94560;color:#fff;font-size:1rem;font-weight:600;cursor:pointer}\nbutton:hover{background:#c73650}\n.error{background:#3d1a1a;border:1px solid #e94560;border-radius:6px;padding:.5rem .75rem;margin-bottom:1rem;font-size:.875rem;color:#ff6b7a}\n</style>\n</head>\n<body>\n<div class=\"card\">\n<h1>Ngino</h1>\n<p class=\"subtitle\">Initial Setup - Create Admin Account</p>\n" + errorHtml + "\n<form method=\"post\" action=\"/admin/setup\" id=\"setupForm\">\n<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"" + HtmlEncode(antiforgeryToken) + "\">\n<label for=\"username\">Username</label>\n<input type=\"text\" id=\"username\" name=\"username\" autocomplete=\"username\" required autofocus>\n<label for=\"email\">Email (optional)</label>\n<input type=\"email\" id=\"email\" name=\"email\" autocomplete=\"email\">\n<label for=\"password\">Password</label>\n<input type=\"password\" id=\"password\" name=\"password\" autocomplete=\"new-password\" required>\n<label for=\"confirmPassword\">Confirm Password</label>\n<input type=\"password\" id=\"confirmPassword\" name=\"confirmPassword\" autocomplete=\"new-password\" required>\n<button type=\"submit\">Create Account</button>\n</form>\n</div>\n<script>\ndocument.getElementById('setupForm').addEventListener('submit',function(e){\nvar p=document.getElementById('password').value;\nvar c=document.getElementById('confirmPassword').value;\nvar msg=[];\nif(p.length<8)msg.push('at least 8 characters');\nif(!/[a-z]/.test(p))msg.push('a lowercase letter');\nif(!/[A-Z]/.test(p))msg.push('an uppercase letter');\nif(!/[0-9]/.test(p))msg.push('a digit');\nif(p!==c)msg.push('passwords must match');\nif(msg.length){e.preventDefault();var d=document.querySelector('.error');if(!d){d=document.createElement('div');d.className='error';document.getElementById('setupForm').parentNode.insertBefore(d,document.getElementById('setupForm'));}d.textContent='Password needs: '+msg.join(', ')+'.';}});\n</script>\n</body>\n</html>";
    }

    private static string? HtmlEncode(string? value) =>
        string.IsNullOrEmpty(value) ? null : System.Net.WebUtility.HtmlEncode(value);

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
                clientTokenConfigured = !string.IsNullOrWhiteSpace(settings.ClientToken),
                userKeysConfigured = store.HasUserKeys,
                clientKeysConfigured = store.HasClientKeys
            },
            management = new
            {
                available = store.IsAvailable,
                databasePath = store.DatabasePath,
                lastError = store.LastError
            },
            clients = BuildClientSummaries(hub, store),
            models = BuildModelSummaries(hub, store),
            userKeys = store.ListUserKeys(),
            clientKeys = store.ListClientKeys(),
            groups = store.ListGroups(),
            userKeyGroups = store.ListUserKeyGroups(),
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
                access.DisabledFromUtc,
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
    string? Reason,
    DateTimeOffset? StartAtUtc,
    DateTimeOffset? UntilUtc);

internal sealed record ModelActionRequest(
    string ClientId,
    string Model,
    string Action);

internal sealed record CreateUserKeyRequest(string? Name);

internal sealed record CreateGroupRequest(string? Name);

internal sealed record UpdateGroupRequest(string Name);

internal sealed record AddGroupClientRequest(
    string? ClientId,
    string? Model,
    string? ClientPattern,
    int? KeepaliveInstancesToKeepAlive,
    int? KeepaliveMaxParallelismPerClient,
    int? KeepaliveParallelismHeadroom);

internal sealed record SetUserKeyGroupsRequest(IReadOnlyList<string>? GroupIds);

internal sealed record UpdateBillingRequest(
    string? Currency,
    double DefaultRatePer1k,
    double RefuseBelowBalance,
    bool Enabled);

internal sealed record AddBillingRuleRequest(
    string ModelRegex,
    double RatePer1k);

internal sealed record UpdateBillingRuleRequest(
    string ModelRegex,
    double RatePer1k);

internal sealed record AddPaymentRequest(
    double Amount,
    string? Description);

internal sealed record ClientSummary(
    string Id,
    bool Connected,
    int PendingRequests,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> ActiveModels,
    DateTimeOffset? ModelsUpdatedAt,
    bool Disabled,
    DateTimeOffset? DisabledFromUtc,
    DateTimeOffset? DisabledUntilUtc,
    bool DisabledManually,
    string? DisabledReason,
    ClientRequestStats RequestStats);

internal sealed record ModelSummary(
    string Name,
    IReadOnlyList<string> ListedClients,
    IReadOnlyList<string> ActiveClients,
    ModelUsageStats Metrics);
