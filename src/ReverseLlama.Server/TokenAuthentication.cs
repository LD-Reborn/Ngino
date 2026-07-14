using ReverseLlama.Protocol;

namespace ReverseLlama.Server;

internal static class TokenAuthentication
{
    private static readonly PathString PathTokenPrefix = new("/token");

    public static bool IsAuthorized(
        HttpRequest request,
        ServerSettings settings,
        ManagementStore managementStore,
        bool allowQueryToken,
        bool allowPathToken = false)
    {
        if (string.IsNullOrWhiteSpace(settings.Token) && !managementStore.HasApiKeys)
        {
            return true;
        }

        if (request.Headers.TryGetValue(ProtocolConstants.TokenHeader, out var headerValues)
            && headerValues.Any(value => IsTokenAuthorized(value, settings, managementStore, updateApiKeyLastUsed: true)))
        {
            return true;
        }

        // Bearer form for OpenAI-compatible clients (e.g. n8n's OpenAI nodes pointed
        // at /clients/{id}/v1) that can send an API key but no custom headers.
        if (request.Headers.TryGetValue("Authorization", out var authorizationValues)
            && authorizationValues.Any(value => TryGetBearerToken(value, out var bearerToken)
                && IsTokenAuthorized(bearerToken, settings, managementStore, updateApiKeyLastUsed: true)))
        {
            return true;
        }

        if (allowPathToken
            && TryGetPathToken(request.Path, out var pathToken, out _)
            && IsTokenAuthorized(pathToken, settings, managementStore, updateApiKeyLastUsed: true))
        {
            return true;
        }

        return allowQueryToken
            && request.Query.TryGetValue("token", out var queryValues)
            && queryValues.Any(value => IsTokenAuthorized(value, settings, managementStore, updateApiKeyLastUsed: true));
    }

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

    public static bool IsTokenAuthorized(
        string? token,
        ServerSettings settings,
        ManagementStore managementStore,
        bool updateApiKeyLastUsed)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(settings.Token)
            && string.Equals(token, settings.Token, StringComparison.Ordinal)
            || managementStore.IsApiKeyValid(token, updateApiKeyLastUsed);
    }

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
