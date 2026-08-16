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
            new KeepaliveCandidate("client-1", true, false),
            new KeepaliveCandidate("client-2", true, false),
            new KeepaliveCandidate("client-3", true, true)
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
            new KeepaliveCandidate("client-1", true, true),
            new KeepaliveCandidate("client-2", true, true)
        };

        var actions = KeepaliveCoordinator.PlanActions([member], candidates);

        Assert.Single(actions);
        Assert.Equal("client-1", actions[0].ClientId);
        Assert.Equal("unload", actions[0].Command);
    }
}
