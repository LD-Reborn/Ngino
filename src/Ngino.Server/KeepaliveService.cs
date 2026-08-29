namespace Ngino.Server;

internal sealed class KeepaliveService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly TunnelHub _hub;
    private readonly ManagementStore _managementStore;
    private readonly ILogger<KeepaliveService> _logger;

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
        if (members.Count == 0)
        {
            return;
        }

        var snapshots = _hub.ClientSnapshots;
        if (snapshots.Count == 0)
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
