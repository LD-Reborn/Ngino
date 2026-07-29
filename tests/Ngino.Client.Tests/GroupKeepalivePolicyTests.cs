using Ngino.Server;
using Xunit;

namespace ReverseLlama.Client.Tests;

public class GroupKeepalivePolicyTests
{
    [Fact]
    public void KeepalivePolicy_IsRoundTripped_ThroughGroupClientInfo()
    {
        var policy = new GroupClientKeepalivePolicy(2, 3, 4);
        var info = new GroupClientInfo(1, "group-1", "client-1", "model-a", "pattern", policy);

        Assert.Equal(2, info.KeepalivePolicy?.InstancesToKeepAlive);
        Assert.Equal(3, info.KeepalivePolicy?.MaxParallelismPerClient);
        Assert.Equal(4, info.KeepalivePolicy?.ParallelismHeadroom);
    }
}
