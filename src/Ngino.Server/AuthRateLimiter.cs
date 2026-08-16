using System.Collections.Concurrent;
using ElmahCore;

namespace Ngino.Server;

internal sealed class AuthRateLimiter
{
    private const int DecayIntervalMinutes = 144; // ~1 step per 2.4 hours

    private readonly ConcurrentDictionary<string, AuthAttemptInfo> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AuthRateLimiter> _logger;
    private readonly ErrorLog _errorLog;

    public AuthRateLimiter(ILogger<AuthRateLimiter> logger, ErrorLog errorLog)
    {
        _logger = logger;
        _errorLog = errorLog;
    }

    public void RecordFailure(string ipAddress, string endpoint)
    {
        var info = _attempts.GetOrAdd(ipAddress, _ => new AuthAttemptInfo());

        lock (info)
        {
            info.Count++;
            info.LastAttemptUtc = DateTime.UtcNow;

            if (info.Count >= 20)
            {
                info.BlockedUntilUtc = DateTime.UtcNow.AddHours(48);
                _logger.LogWarning(
                    "IP {IpAddress} blocked for 48 hours after {Count} failed auth attempts (last: {Endpoint})",
                    ipAddress, info.Count, endpoint);
            }
            else
            {
                _logger.LogWarning(
                    "Failed auth attempt #{Count} from {IpAddress} on {Endpoint}",
                    info.Count, ipAddress, endpoint);
            }

            _errorLog.Log(new Error(new AuthFailureException(ipAddress, endpoint, info.Count)));
        }
    }

    public void RecordSuccess(string ipAddress)
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
                    "Auth success from {IpAddress}: count reduced from {Before} to {After}",
                    ipAddress, before, info.Count);
            }
        }
    }

    public (bool Allowed, TimeSpan? RetryAfter, bool IsBlocked) CheckRateLimit(string ipAddress)
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
                    return (false, wait - elapsed, false);
                }
            }

            return (true, null, false);
        }
    }

    public static string GetClientIp(HttpRequest request) =>
        request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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
