using System.Collections.Concurrent;
using ElmahCore;
using Ngino.Protocol;

namespace Ngino.Server;

internal sealed class AuthRateLimiter
{
    public const string IdentityItemKey = "Ngino.AuthRateLimiter.Identity";
    public const string UsernameItemKey = "Ngino.AuthRateLimiter.Username";

    private const int DecayIntervalMinutes = 144; // ~1 step per 2.4 hours

    private readonly ConcurrentDictionary<string, AuthAttemptInfo> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AuthRateLimiter> _logger;
    private readonly ErrorLog _errorLog;

    public AuthRateLimiter(ILogger<AuthRateLimiter> logger, ErrorLog errorLog)
    {
        _logger = logger;
        _errorLog = errorLog;
    }

    public void RecordFailure(string ipAddress, string endpoint, AuthAttemptIdentity? identity = null)
    {
        var info = _attempts.GetOrAdd(ipAddress, _ => new AuthAttemptInfo());

        lock (info)
        {
            info.Count++;
            info.LastAttemptUtc = DateTime.UtcNow;

            var suffix = BuildIdentitySuffix(identity);
            if (info.Count >= 20)
            {
                info.BlockedUntilUtc = DateTime.UtcNow.AddHours(48);
                _logger.LogWarning(
                    "IP {IpAddress} blocked for 48 hours after {Count} failed auth attempts (last: {Endpoint}){Identity}",
                    ipAddress, info.Count, endpoint, suffix);
            }
            else
            {
                _logger.LogWarning(
                    "Failed auth attempt #{Count} from {IpAddress} on {Endpoint}{Identity}",
                    info.Count, ipAddress, endpoint, suffix);
            }

            _errorLog.Log(new Error(new AuthFailureException(ipAddress, endpoint, info.Count)));
        }
    }

    public void RecordSuccess(string ipAddress, AuthAttemptIdentity? identity = null)
    {
        if (!_attempts.TryGetValue(ipAddress, out var info))
            return;

        lock (info)
        {
            if (info.Count > 0)
            {
                var before = info.Count;
                info.Count /= 2;
                info.LastAttemptUtc = DateTime.UtcNow;
                _logger.LogInformation(
                    "Auth success from {IpAddress}: count reduced from {Before} to {After}{Identity}",
                    ipAddress, before, info.Count, BuildIdentitySuffix(identity));
            }
        }
    }

    public (bool Allowed, TimeSpan? RetryAfter, bool IsBlocked) CheckRateLimit(string ipAddress, AuthAttemptIdentity? identity = null)
    {
        if (!_attempts.TryGetValue(ipAddress, out var info))
        {
            return (true, null, false);
        }

        lock (info)
        {
            if (info.BlockedUntilUtc is { } blockedUntil)
            {
                if (blockedUntil > DateTime.UtcNow)
                {
                    _logger.LogWarning(
                        "Rate limit: IP {IpAddress} is blocked, {Remaining} remaining{Identity}",
                        ipAddress, blockedUntil - DateTime.UtcNow, BuildIdentitySuffix(identity));
                    return (false, blockedUntil - DateTime.UtcNow, true);
                }

                info.Count = 0;
                info.BlockedUntilUtc = null;
                info.LastAttemptUtc = DateTime.MinValue;
                return (true, null, false);
            }

            if (info.Count > 0)
            {
                var elapsed = DateTime.UtcNow - info.LastAttemptUtc;
                var decayTicks = (int)(elapsed.TotalMinutes / DecayIntervalMinutes);
                if (decayTicks > 0)
                {
                    info.Count = Math.Max(0, info.Count - decayTicks);
                }
            }

            var waitTime = CalculateWaitTime(info.Count);
            if (waitTime is { } wait)
            {
                var elapsed = DateTime.UtcNow - info.LastAttemptUtc;
                if (elapsed < wait)
                {
                    _logger.LogWarning(
                        "Rate limit: IP {IpAddress} must wait {Remaining} (attempt count: {Count}){Identity}",
                        ipAddress, wait - elapsed, info.Count, BuildIdentitySuffix(identity));
                    return (false, wait - elapsed, false);
                }
            }

            return (true, null, false);
        }
    }

    public static string GetClientIp(HttpRequest request) =>
        request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static AuthAttemptIdentity? ResolveIdentity(HttpContext context)
    {
        var request = context.Request;
        var username = context.Items.TryGetValue(UsernameItemKey, out var usernameValue)
            ? usernameValue as string
            : null;

        var clientId = request.Headers[ProtocolConstants.ClientIdHeader].FirstOrDefault();

        var keyPrefix = FirstPresentedKeyPrefix(request);

        return username is null
            && string.IsNullOrEmpty(clientId)
            && keyPrefix is null
                ? null
                : new AuthAttemptIdentity(username, string.IsNullOrEmpty(clientId) ? null : clientId, keyPrefix);
    }

    private static string? FirstPresentedKeyPrefix(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ProtocolConstants.TokenHeader, out var headerValues))
        {
            foreach (var value in headerValues)
            {
                if (TryGetKeyPrefix(value, out var prefix))
                {
                    return prefix;
                }
            }
        }

        if (request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            foreach (var value in authorizationValues)
            {
                if (TryGetBearer(value, out var token) && TryGetKeyPrefix(token, out var prefix))
                {
                    return prefix;
                }
            }
        }

        if (request.Query.TryGetValue("token", out var queryValues))
        {
            foreach (var value in queryValues)
            {
                if (TryGetKeyPrefix(value, out var prefix))
                {
                    return prefix;
                }
            }
        }

        return null;
    }

    private static bool TryGetBearer(string? authorization, out string token)
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

    private static bool TryGetKeyPrefix(string? key, out string prefix)
    {
        prefix = "";
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        prefix = key.Length <= 12 ? key : key[..12];
        return true;
    }

    private static string BuildIdentitySuffix(AuthAttemptIdentity? identity)
    {
        if (identity is null)
        {
            return "";
        }

        var parts = new List<string>();
        if (identity.Username is not null)
        {
            parts.Add($"username={identity.Username}");
        }

        if (identity.ClientId is not null)
        {
            parts.Add($"clientId={identity.ClientId}");
        }

        if (identity.KeyPrefix is not null)
        {
            parts.Add($"keyPrefix={identity.KeyPrefix}");
        }

        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
    }

    private static TimeSpan? CalculateWaitTime(int attemptCount) =>
        attemptCount switch
        {
            < 3 => null,
            < 5 => TimeSpan.FromSeconds(5),
            < 10 => TimeSpan.FromSeconds(5 + (attemptCount - 5) * 5),
            < 20 => TimeSpan.FromMinutes(attemptCount - 9),
            _ => null
        };

    private sealed class AuthAttemptInfo
    {
        public int Count;
        public DateTime LastAttemptUtc;
        public DateTime? BlockedUntilUtc;
    }

    private sealed class AuthFailureException(string ipAddress, string endpoint, int attemptCount)
        : Exception($"Failed auth attempt #{attemptCount} from {ipAddress} on {endpoint}");
}

internal sealed record AuthAttemptIdentity(
    string? Username,
    string? ClientId,
    string? KeyPrefix);
