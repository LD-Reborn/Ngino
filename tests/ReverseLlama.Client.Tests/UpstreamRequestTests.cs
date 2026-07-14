using ReverseLlama.Client;
using Xunit;

namespace ReverseLlama.Client.Tests;

public sealed class UpstreamRequestTests
{
    private static readonly Uri Upstream = new("http://localhost:11434");

    [Theory]
    [InlineData("/api/tags", "http://localhost:11434/api/tags")]
    [InlineData("/api/tags?model=llama3.1", "http://localhost:11434/api/tags?model=llama3.1")]
    [InlineData("/api//tags", "http://localhost:11434/api//tags")]
    public void BuildUpstreamUri_AcceptsOriginFormPaths(string pathAndQuery, string expected)
    {
        var uri = UpstreamRequest.BuildUpstreamUri(Upstream, pathAndQuery);

        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("//169.254.169.254/latest")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("https://localhost:11434/api/tags")]
    [InlineData(@"\\169.254.169.254\latest")]
    [InlineData(@"/\169.254.169.254/latest")]
    [InlineData("api/tags")]
    public void BuildUpstreamUri_RejectsPathsThatCanEscapeTheUpstreamOrigin(string pathAndQuery)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => UpstreamRequest.BuildUpstreamUri(Upstream, pathAndQuery));

        Assert.Contains("origin-form path", exception.Message);
    }
}
