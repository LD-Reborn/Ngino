using System.Text.RegularExpressions;

namespace Ngino.Server;

internal static class KeepaliveCoordinator
{
    public static IReadOnlyList<KeepaliveAction> PlanActions(
        IEnumerable<GroupClientInfo> members,
        IEnumerable<KeepaliveCandidate> candidates)
    {
        var rows = members
            .Where(member => !string.IsNullOrWhiteSpace(member.Model))
            .OrderBy(member => member.Id)
            .ToList();

        var slots = BuildSlots(candidates);
        if (rows.Count == 0 || slots.Count == 0)
        {
            return [];
        }

        var key = static (Slot slot) => slot.Key;
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Least-churn seed: every currently active slot stays desired unless a rule trims it.
        foreach (var slot in slots.Where(slot => slot.Active))
        {
            desired.Add(key(slot));
        }

        // Precompute which rules cover each slot so overlaps are resolved once.
        var coveringRows = new Dictionary<string, List<GroupClientInfo>>(slots.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            var covering = new List<GroupClientInfo>(rows.Count);
            foreach (var row in rows)
            {
                if (SlotCoveredBy(row, slot))
                {
                    covering.Add(row);
                }
            }

            coveringRows[slot.Key] = covering;
        }

        bool IsCoveredBy(Slot slot, GroupClientInfo row) => coveringRows[slot.Key].Contains(row);
        bool IsUniquelyCovered(Slot slot) => coveringRows[slot.Key].Count == 1;

        // Trim: a rule may shrink warm slots only where it is the sole rule (no overlap),
        // down to its instance target. Overlapping slots stay untouched, so rules never fight.
        foreach (var row in rows)
        {
            var target = TargetInstances(row);
            var warm = slots
                .Where(slot => IsUniquelyCovered(slot) && IsCoveredBy(slot, row))
                .Where(slot => slot.Active && desired.Contains(key(slot)))
                .OrderBy(slot => slot.ClientId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(slot => slot.Model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var surplus = warm.Count - target;
            foreach (var slot in warm.Take(surplus))
            {
                desired.Remove(key(slot));
            }
        }

        // Grow: fill unmet demand, preferring lexicographically earlier slots, until a fixed point.
        bool changed;
        do
        {
            changed = false;
            foreach (var row in rows)
            {
                var target = TargetInstances(row);
                if (target <= 0)
                {
                    continue;
                }

                var covered = slots.Where(slot => IsCoveredBy(slot, row)).ToList();
                var warmCount = covered.Count(slot => desired.Contains(key(slot)));
                var needed = target - warmCount;
                if (needed <= 0)
                {
                    continue;
                }

                var toLoad = covered
                    .Where(slot => !desired.Contains(key(slot)))
                    .OrderBy(slot => slot.ClientId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(slot => slot.Model, StringComparer.OrdinalIgnoreCase)
                    .Take(needed)
                    .ToList();

                foreach (var slot in toLoad)
                {
                    if (desired.Add(key(slot)))
                    {
                        changed = true;
                    }
                }
            }
        } while (changed);

        var actions = new List<KeepaliveAction>(slots.Count);

        foreach (var slot in slots
            .Where(slot => slot.Active && !desired.Contains(key(slot)))
            .OrderBy(slot => slot.ClientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(slot => slot.Model, StringComparer.OrdinalIgnoreCase))
        {
            actions.Add(new KeepaliveAction(slot.ClientId, "unload", slot.Model));
        }

        foreach (var slot in slots
            .Where(slot => !slot.Active && slot.Listed && desired.Contains(key(slot)))
            .OrderBy(slot => slot.ClientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(slot => slot.Model, StringComparer.OrdinalIgnoreCase))
        {
            actions.Add(new KeepaliveAction(slot.ClientId, "load", slot.Model));
        }

        return actions;
    }

    private static int TargetInstances(GroupClientInfo member) =>
        Math.Max(0, member.KeepalivePolicy?.InstancesToKeepAlive ?? GroupClientKeepalivePolicy.Default.InstancesToKeepAlive);

    private static List<Slot> BuildSlots(IEnumerable<KeepaliveCandidate> candidates)
    {
        var distinct = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate.ListedModel is null && candidate.ActiveModel is null)
            {
                continue;
            }

            var model = candidate.ListedModel ?? candidate.ActiveModel!;
            var slot = new Slot(
                candidate.ClientId,
                model,
                Listed: candidate.HasListedModel,
                Active: candidate.HasActiveModel);

            if (distinct.TryGetValue(slot.Key, out var existing))
            {
                distinct[slot.Key] = existing with
                {
                    Listed = existing.Listed || slot.Listed,
                    Active = existing.Active || slot.Active
                };
            }
            else
            {
                distinct[slot.Key] = slot;
            }
        }

        return distinct.Values
            .OrderBy(slot => slot.ClientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(slot => slot.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SlotCoveredBy(GroupClientInfo member, Slot slot) =>
        MatchesMember(slot.ClientId, member)
        && GroupAccess.ModelSelectorMatches(member.Model!, slot.Model);

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

    private readonly record struct Slot(string ClientId, string Model, bool Listed, bool Active)
    {
        public string Key => $"{ClientId}\u0000{Model}";
    }
}

internal sealed record KeepaliveCandidate(
    string ClientId,
    string? ListedModel,
    string? ActiveModel)
{
    public bool HasListedModel => ListedModel is not null;

    public bool HasActiveModel => ActiveModel is not null;
}

internal sealed record KeepaliveAction(
    string ClientId,
    string Command,
    string Model);