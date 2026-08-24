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
}
