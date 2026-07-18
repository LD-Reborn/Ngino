using System.Collections.Concurrent;

namespace ReverseLlama.Server;

internal sealed class AuthRateLimiter
{
    private readonly ConcurrentDictionary<string, AuthAttemptInfo> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AuthRateLimiter> _logger;

    public AuthRateLimiter(ILogger<AuthRateLimiter> logger)
    {
        _logger = logger;
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

    public static string GetClientIp(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var first = forwardedFor.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                var commaIndex = first.IndexOf(',');
                return commaIndex > 0 ? first[..commaIndex].Trim() : first.Trim();
            }
        }

        if (request.Headers.TryGetValue("X-Real-IP", out var realIp))
        {
            var first = realIp.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first.Trim();
            }
        }

        return request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
}
