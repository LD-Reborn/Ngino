using Ngino.Server;
using Xunit;

namespace Ngino.Client.Tests;

public class KeepaliveCoordinatorTests
{
    [Fact]
    public void PlanActions_LoadsMissingKeepaliveInstances()
    {
        var policy = new GroupClientKeepalivePolicy(2, 1, 1);
        var member = new GroupClientInfo(1, "group-1", "client-1", "bge-m3:latest", null, policy);
        var candidates = new[]
        {
            new KeepaliveCandidate("client-1", "bge-m3:latest", null),
            new KeepaliveCandidate("client-2", "bge-m3:latest", null),
            new KeepaliveCandidate("client-3", "bge-m3:latest", "bge-m3:latest")
        };

        var actions = KeepaliveCoordinator.PlanActions([member], candidates);

        Assert.Single(actions);
        Assert.Equal("client-1", actions[0].ClientId);
        Assert.Equal("load", actions[0].Command);
        Assert.Equal("bge-m3:latest", actions[0].Model);
    }

    [Fact]
    public void PlanActions_UnloadsWhenTooManyInstancesAreActive()
    {
        var policy = new GroupClientKeepalivePolicy(1, 1, 1);
        var member = new GroupClientInfo(2, "group-1", null, "bge-m3:latest", null, policy);
        var candidates = new[]
        {
            new KeepaliveCandidate("client-1", "bge-m3:latest", "bge-m3:latest"),
            new KeepaliveCandidate("client-2", "bge-m3:latest", "bge-m3:latest")
        };

        var actions = KeepaliveCoordinator.PlanActions([member], candidates);

        Assert.Single(actions);
        Assert.Equal("client-1", actions[0].ClientId);
        Assert.Equal("unload", actions[0].Command);
        Assert.Equal("bge-m3:latest", actions[0].Model);
    }

    [Fact]
    public void PlanActions_ExpandsRegexSelectorToConcreteModelName()
    {
        var policy = new GroupClientKeepalivePolicy(1, 1, 1);
        var member = new GroupClientInfo(3, "group-1", null, "qwen3-embed.*", null, policy);
        var candidates = new[]
        {
            new KeepaliveCandidate(
                "client-1",
                ListedModel: "qwen3-embedding:8b-fp16",
                ActiveModel: null)
        };

        var actions = KeepaliveCoordinator.PlanActions([member], candidates);

        Assert.Single(actions);
        Assert.Equal("load", actions[0].Command);
        Assert.Equal("qwen3-embedding:8b-fp16", actions[0].Model);
    }

    [Fact]
    public void PlanActions_ZeroInstancesNeverLoadsButUnloadsSurplusActive()
    {
        var policy = new GroupClientKeepalivePolicy(0, 1, 0);
        var member = new GroupClientInfo(4, "group-1", null, ".*", null, policy);
        var candidates = new[]
        {
            new KeepaliveCandidate("client-1", "bge-m3:latest", null),
            new KeepaliveCandidate("client-2", "bge-m3:latest", "bge-m3:latest")
        };

        var actions = KeepaliveCoordinator.PlanActions([member], candidates);

        var action = Assert.Single(actions);
        Assert.Equal("client-2", action.ClientId);
        Assert.Equal("unload", action.Command);
        Assert.Equal("bge-m3:latest", action.Model);
    }

    [Fact]
    public void PlanActions_NoListedMatchEmitsNoActions()
    {
        var policy = new GroupClientKeepalivePolicy(1, 1, 1);
        var member = new GroupClientInfo(5, "group-1", "client-1", "missing-model.*", null, policy);
        var candidates = new[]
        {
            new KeepaliveCandidate("client-1", null, null)
        };

        var actions = KeepaliveCoordinator.PlanActions([member], candidates);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_CatchAllZeroInstancesDoesNotUnloadModelsKeptWarmByAnotherRule()
    {
        var catchAll = new GroupClientKeepalivePolicy(0, 1, 0);
        var warm = new GroupClientKeepalivePolicy(2, 1, 0);
        var members = new[]
        {
            new GroupClientInfo(1, "all", "kraken", ".*", null, catchAll),
            new GroupClientInfo(2, "embed", "kraken", "bge-m3:latest", null, warm)
        };
        var candidates = new[]
        {
            new KeepaliveCandidate("kraken", "bge-m3:latest", "bge-m3:latest")
        };

        var actions = KeepaliveCoordinator.PlanActions(members, candidates);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_CatchAllZeroInstancesAllowsAnotherRuleToLoadMissingWarmInstance()
    {
        var catchAll = new GroupClientKeepalivePolicy(0, 1, 0);
        var warm = new GroupClientKeepalivePolicy(1, 1, 0);
        var members = new[]
        {
            new GroupClientInfo(1, "all", "kraken", ".*", null, catchAll),
            new GroupClientInfo(2, "embed", "kraken", "bge-m3:latest", null, warm)
        };
        var candidates = new[]
        {
            new KeepaliveCandidate("kraken", "bge-m3:latest", null)
        };

        var actions = KeepaliveCoordinator.PlanActions(members, candidates);

        var action = Assert.Single(actions);
        Assert.Equal("kraken", action.ClientId);
        Assert.Equal("load", action.Command);
        Assert.Equal("bge-m3:latest", action.Model);
    }

    [Fact]
    public void PlanActions_OverlappingWarmRulesConvergeWithoutFighting()
    {
        var warm = new GroupClientKeepalivePolicy(1, 1, 0);
        var members = new[]
        {
            new GroupClientInfo(1, "group-a", null, "bge-m3:latest", null, warm),
            new GroupClientInfo(2, "group-b", null, "bge-m3:latest", null, warm)
        };
        var candidates = new[]
        {
            new KeepaliveCandidate("client-1", "bge-m3:latest", "bge-m3:latest"),
            new KeepaliveCandidate("client-2", "bge-m3:latest", "bge-m3:latest")
        };

        var actions = KeepaliveCoordinator.PlanActions(members, candidates);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_TrimsSurplusOnlyOnUniquelyCoveredSlots()
    {
        var catchAll = new GroupClientKeepalivePolicy(0, 1, 0);
        var warm = new GroupClientKeepalivePolicy(1, 1, 0);
        var members = new[]
        {
            new GroupClientInfo(1, "all", null, ".*", null, catchAll),
            new GroupClientInfo(2, "embed", null, "bge-m3:latest", null, warm)
        };
        var candidates = new[]
        {
            new KeepaliveCandidate("client-1", "bge-m3:latest", "bge-m3:latest"),
            new KeepaliveCandidate("client-2", "bge-m3:latest", "bge-m3:latest"),
            new KeepaliveCandidate("client-3", "loop:latest", "loop:latest"),
            new KeepaliveCandidate("client-4", "other:latest", "other:latest")
        };

        var actions = KeepaliveCoordinator.PlanActions(members, candidates);

        Assert.Equal(2, actions.Count);
        Assert.All(actions, action => Assert.Equal("unload", action.Command));
        Assert.DoesNotContain(actions, action => action.Model == "bge-m3:latest");
        Assert.DoesNotContain(actions, action => action.ClientId is "client-1" or "client-2");
    }
}
