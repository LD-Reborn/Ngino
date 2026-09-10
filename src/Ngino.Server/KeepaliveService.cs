using System.Collections.Concurrent;

namespace Ngino.Server;

internal sealed class KeepaliveService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ModelLockDuration = TimeSpan.FromSeconds(60);

    private readonly TunnelHub _hub;
    private readonly ManagementStore _managementStore;
    private readonly ILogger<KeepaliveService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _modelLocks = new(StringComparer.OrdinalIgnoreCase);

    public KeepaliveService(TunnelHub hub, ManagementStore managementStore, ILogger<KeepaliveService> logger)
    {
        _hub = hub;
        _managementStore = managementStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyKeepaliveAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Keepalive cycle failed.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task ApplyKeepaliveAsync(CancellationToken cancellationToken)
    {
        var members = _managementStore.ListAllGroupClients();
        var snapshots = _hub.ClientSnapshots;

        CleanupLocks(snapshots);

        if (members.Count == 0 || snapshots.Count == 0)
        {
            return;
        }

        var clientWarmth = _managementStore.ListClientControls();
        var modelWarmth = _managementStore.ListClientModelWarmth()
            .ToLookup(entry => entry.ClientId, StringComparer.OrdinalIgnoreCase);

        var candidates = snapshots
            .SelectMany(snapshot => BuildCandidates(snapshot, clientWarmth, modelWarmth))
            .OrderBy(candidate => candidate.ClientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ListedModel ?? candidate.ActiveModel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actions = KeepaliveCoordinator.PlanActions(members, candidates);
        foreach (var action in actions)
        {
            if (IsModelLocked(action.Model))
            {
                _logger.LogInformation(
                    "Skipping keepalive {Command} for model {Model} on client {ClientId}: model is locked after a num_ctx request.",
                    action.Command,
                    action.Model,
                    action.ClientId);
                continue;
            }

            try
            {
                var connection = _hub.Get(action.ClientId);
                if (connection is null)
                {
                    continue;
                }

                var response = await connection.SendModelCommandAsync(
                    action.Command,
                    action.Model,
                    payloadJson: null,
                    CommandTimeout,
                    cancellationToken);

                if (response.StatusCode is < 200 or >= 300)
                {
                    _logger.LogWarning(
                        "Keepalive {Command} for model {Model} on client {ClientId} returned HTTP {StatusCode}.",
                        action.Command,
                        action.Model,
                        action.ClientId,
                        response.StatusCode);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Keepalive {Command} for model {Model} on client {ClientId} failed.",
                    action.Command,
                    action.Model,
                    action.ClientId);
            }
        }
    }

    internal void LockModel(string model, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var normalized = model.Trim();
        var expiresAt = DateTime.UtcNow + (duration ?? ModelLockDuration);
        _modelLocks[normalized] = expiresAt;
        _logger.LogInformation(
            "Keepalive actions for model {Model} are locked until {ExpiresAt:O} after a num_ctx request.",
            normalized,
            expiresAt);
    }

    private void CleanupLocks(IReadOnlyList<TunnelClientSnapshot> snapshots)
    {
        if (_modelLocks.IsEmpty)
        {
            return;
        }

        var now = DateTime.UtcNow;

        var activeModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var activeModel in snapshot.ActiveModels)
            {
                activeModels.Add(activeModel);
            }
        }

        foreach (var locked in _modelLocks.ToArray())
        {
            var expired = locked.Value <= now;
            var loadedAgain = activeModels.Contains(locked.Key);
            if (!expired && !loadedAgain)
            {
                continue;
            }

            if (_modelLocks.TryRemove(new KeyValuePair<string, DateTime>(locked.Key, locked.Value)))
            {
                _logger.LogInformation(
                    "Released keepalive lock for model {Model} ({Reason}).",
                    locked.Key,
                    expired ? "expired" : "model is active again");
            }
        }
    }

    private bool IsModelLocked(string model) =>
        !string.IsNullOrWhiteSpace(model) && _modelLocks.ContainsKey(model.Trim());

    private static IEnumerable<KeepaliveCandidate> BuildCandidates(
        TunnelClientSnapshot snapshot,
        IReadOnlyDictionary<string, ClientAccess> clientWarmth,
        ILookup<string, ClientModelWarmth> modelWarmth)
    {
        var models = new HashSet<string>(snapshot.Models, StringComparer.OrdinalIgnoreCase);
        models.UnionWith(snapshot.ActiveModels);

        var baseWarmth = clientWarmth.TryGetValue(snapshot.Id, out var access) ? access.Warmth : 0;
        var overrides = modelWarmth[snapshot.Id].ToDictionary(
            entry => entry.Model,
            entry => entry.Warmth,
            StringComparer.OrdinalIgnoreCase);

        foreach (var model in models.OrderBy(model => model, StringComparer.OrdinalIgnoreCase))
        {
            var listed = snapshot.Models.Contains(model, StringComparer.OrdinalIgnoreCase);
            var active = snapshot.ActiveModels.Contains(model, StringComparer.OrdinalIgnoreCase);
            var warmth = baseWarmth + (overrides.TryGetValue(model, out var overrideWarmth) ? overrideWarmth : 0);
            yield return new KeepaliveCandidate(
                snapshot.Id,
                listed ? model : null,
                active ? model : null,
                warmth);
        }
    }
}
