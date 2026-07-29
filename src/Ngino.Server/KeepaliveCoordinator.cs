using System.Text.RegularExpressions;

namespace Ngino.Server;

internal static class KeepaliveCoordinator
{
    public static IReadOnlyList<KeepaliveAction> PlanActions(
        IEnumerable<GroupClientInfo> members,
        IEnumerable<KeepaliveCandidate> candidates)
    {
        var actions = new List<KeepaliveAction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateList = candidates.ToList();

        foreach (var member in members)
        {
            if (string.IsNullOrWhiteSpace(member.Model))
            {
                continue;
            }

            var policy = member.KeepalivePolicy ?? GroupClientKeepalivePolicy.Default;
            var targetCount = Math.Max(0, policy.InstancesToKeepAlive);
            var matching = candidateList
                .Where(candidate => MatchesMember(candidate.ClientId, member))
                .ToList();

            if (matching.Count == 0)
            {
                continue;
            }

            var active = matching.Where(candidate => candidate.HasActiveModel).ToList();
            var activeCount = active.Count;

            if (activeCount < targetCount)
            {
                var toLoad = matching
                    .Where(candidate => !candidate.HasActiveModel && candidate.HasListedModel)
                    .OrderBy(candidate => candidate.ClientId, StringComparer.OrdinalIgnoreCase)
                    .Take(targetCount - activeCount);

                foreach (var candidate in toLoad)
                {
                    var key = $"{candidate.ClientId}:{member.Model}";
                    if (seen.Add(key))
                    {
                        actions.Add(new KeepaliveAction(candidate.ClientId, "load", member.Model));
                    }
                }
            }
            else if (activeCount > targetCount)
            {
                var toUnload = active
                    .OrderByDescending(candidate => candidate.ClientId, StringComparer.OrdinalIgnoreCase)
                    .Skip(targetCount)
                    .Take(activeCount - targetCount);

                foreach (var candidate in toUnload)
                {
                    var key = $"{candidate.ClientId}:{member.Model}";
                    if (seen.Add(key))
                    {
                        actions.Add(new KeepaliveAction(candidate.ClientId, "unload", member.Model));
                    }
                }
            }
        }

        return actions;
    }

    private static bool MatchesMember(string clientId, GroupClientInfo member)
    {
        if (string.IsNullOrWhiteSpace(member.ClientId)
            && string.IsNullOrWhiteSpace(member.ClientPattern))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(member.ClientId)
            && string.Equals(clientId, member.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(member.ClientPattern))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(clientId, member.ClientPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch (RegexParseException)
        {
            return false;
        }
    }
}

internal sealed record KeepaliveCandidate(
    string ClientId,
    bool HasListedModel,
    bool HasActiveModel);

internal sealed record KeepaliveAction(
    string ClientId,
    string Command,
    string Model);
