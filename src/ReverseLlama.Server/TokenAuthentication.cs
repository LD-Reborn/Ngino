using System.Security.Cryptography;
using System.Text;
using ReverseLlama.Protocol;

namespace ReverseLlama.Server;

internal static class TokenAuthentication
{
    private static readonly PathString PathTokenPrefix = new("/token");

    public static AuthResult Authorize(
        HttpRequest request,
        ServerSettings settings,
        ManagementStore managementStore,
        bool allowQueryToken,
        bool allowPathToken = false)
    {
        if (string.IsNullOrWhiteSpace(settings.Token) && !managementStore.HasApiKeys)
        {
            return AuthResult.Success(null);
        }

        if (request.Headers.TryGetValue(ProtocolConstants.TokenHeader, out var headerValues))
        {
            foreach (var value in headerValues)
            {
                var result = AuthorizeToken(value, settings, managementStore, updateApiKeyLastUsed: true);
                if (result.IsAuthorized)
                {
                    return result;
                }
            }
        }

        if (request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            foreach (var value in authorizationValues)
            {
                if (TryGetBearerToken(value, out var bearerToken))
                {
                    var result = AuthorizeToken(bearerToken, settings, managementStore, updateApiKeyLastUsed: true);
                    if (result.IsAuthorized)
                    {
                        return result;
                    }
                }
            }
        }

        // Path-token auth: useful for clients that cannot send headers.
        // SECURITY: the token appears in the URL and will be logged by
        // web servers, proxies, and browsers. Prefer header auth when possible.
        if (allowPathToken
            && TryGetPathToken(request.Path, out var pathToken, out _))
        {
            var result = AuthorizeToken(pathToken, settings, managementStore, updateApiKeyLastUsed: true);
            if (result.IsAuthorized)
            {
                return result;
            }
        }

        // Query-string auth: needed for clients that cannot send headers
        // (e.g. browser address bar, status page).
        // SECURITY: same URL-logging risks as path-token auth above.
        if (allowQueryToken
            && request.Query.TryGetValue("token", out var queryValues))
        {
            foreach (var value in queryValues)
            {
                var result = AuthorizeToken(value, settings, managementStore, updateApiKeyLastUsed: true);
                if (result.IsAuthorized)
                {
                    return result;
                }
            }
        }

        return AuthResult.Failure;
    }

    public static bool IsAuthorized(
        HttpRequest request,
        ServerSettings settings,
        ManagementStore managementStore,
        bool allowQueryToken,
        bool allowPathToken = false) =>
        Authorize(request, settings, managementStore, allowQueryToken, allowPathToken).IsAuthorized;

    public static bool TryRemovePathToken(
        PathString path,
        ServerSettings settings,
        ManagementStore managementStore,
        out PathString remainingPath)
    {
        remainingPath = path;

        if (!TryGetPathToken(path, out var pathToken, out var tokenRemainingPath)
            || !IsTokenAuthorized(pathToken, settings, managementStore, updateApiKeyLastUsed: false))
        {
            return false;
        }

        remainingPath = string.IsNullOrEmpty(tokenRemainingPath.Value)
            ? new PathString("/")
            : tokenRemainingPath;
        return true;
    }

    public static bool IsOwnBearerValue(string? value, ServerSettings settings, ManagementStore managementStore) =>
        TryGetBearerToken(value, out var token)
        && IsTokenAuthorized(token, settings, managementStore, updateApiKeyLastUsed: false);

    private static AuthResult AuthorizeToken(
        string? token,
        ServerSettings settings,
        ManagementStore managementStore,
        bool updateApiKeyLastUsed)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthResult.Failure;
        }

        if (!string.IsNullOrWhiteSpace(settings.Token)
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token))))
        {
            return AuthResult.Success(null);
        }

        var apiKeyId = managementStore.GetApiKeyId(token);
        if (apiKeyId is not null)
        {
            managementStore.IsApiKeyValid(token, updateApiKeyLastUsed);
            return AuthResult.Success(apiKeyId);
        }

        return AuthResult.Failure;
    }

    public static bool IsTokenAuthorized(
        string? token,
        ServerSettings settings,
        ManagementStore managementStore,
        bool updateApiKeyLastUsed) =>
        AuthorizeToken(token, settings, managementStore, updateApiKeyLastUsed).IsAuthorized;

    private static bool TryGetBearerToken(string? authorization, out string token)
    {
        token = "";

        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = authorization["Bearer ".Length..].Trim();
        return token.Length > 0;
    }

    private static bool TryGetPathToken(PathString path, out string pathToken, out PathString remainingPath)
    {
        pathToken = "";
        remainingPath = PathString.Empty;

        if (!path.StartsWithSegments(PathTokenPrefix, out var pathAfterPrefix))
        {
            return false;
        }

        var value = pathAfterPrefix.Value ?? "";
        if (value.Length <= 1 || value[0] != '/')
        {
            return false;
        }

        var nextSlash = value.IndexOf('/', 1);
        pathToken = nextSlash < 0
            ? value[1..]
            : value[1..nextSlash];

        if (string.IsNullOrEmpty(pathToken))
        {
            return false;
        }

        remainingPath = nextSlash < 0
            ? PathString.Empty
            : new PathString(value[nextSlash..]);
        return true;
    }
}

internal sealed class AuthResult
{
    public static AuthResult Failure { get; } = new(false, null);

    public static AuthResult Success(string? apiKeyId) => new(true, apiKeyId);

    public bool IsAuthorized { get; }

    public string? ApiKeyId { get; }

    private AuthResult(bool isAuthorized, string? apiKeyId)
    {
        IsAuthorized = isAuthorized;
        ApiKeyId = apiKeyId;
    }
}
