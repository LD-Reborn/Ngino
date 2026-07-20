using Ngino.Client;
using Xunit;

namespace Ngino.Client.Tests;

public sealed class ClientOptionsTests
{
    [Fact]
    public void InsecureTlsIsDisabledByDefault()
    {
        var options = ClientOptions.Parse([]);

        Assert.False(options.InsecureSkipTlsVerify);
    }

    [Fact]
    public void InsecureTlsCanBeEnabledWithFlag()
    {
        var options = ClientOptions.Parse(["--insecure-skip-tls-verify"]);

        Assert.True(options.InsecureSkipTlsVerify);
    }

    [Fact]
    public void InsecureTlsCanBeEnabledWithEnvironmentStyleValue()
    {
        var options = ClientOptions.Parse(["--insecure-skip-tls-verify=true"]);

        Assert.True(options.InsecureSkipTlsVerify);
    }
}
