using System.Text.Json;
using ReverseLlama.Client;
using Xunit;

namespace ReverseLlama.Client.Tests;

public sealed class TunnelClientModelTests
{
    private static readonly Uri Upstream = new("http://localhost:11434");

    [Fact]
    public void ExtractModelNames_ReadsActiveOllamaPsModelNames()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "models": [
                { "name": "qwen3.5:0.8b", "model": "qwen3.5:0.8b" },
                { "name": "llama3.2:latest", "model": "llama3.2:latest" },
                { "name": " QWEN3.5:0.8B " }
              ]
            }
            """);

        var models = TunnelClient.ExtractModelNames(document.RootElement);

        Assert.Equal(["llama3.2:latest", "qwen3.5:0.8b"], models);
    }

    [Fact]
    public async Task BuildModelCommandRequest_LoadUsesOllamaPreloadRequest()
    {
        using var request = TunnelClient.BuildModelCommandRequest(Upstream, "load", " qwen3.5:0.8b ");
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/api/generate", request.RequestUri!.AbsoluteUri);
        Assert.Equal("qwen3.5:0.8b", document.RootElement.GetProperty("model").GetString());
        Assert.False(document.RootElement.TryGetProperty("prompt", out _));
        Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(-1, document.RootElement.GetProperty("keep_alive").GetInt32());
    }

    [Theory]
    [InlineData("load", -1)]
    [InlineData("unload", 0)]
    public async Task BuildEmbeddingModelCommandRequest_UsesOllamaEmbedWarmupRequest(
        string command,
        int expectedKeepAlive)
    {
        using var request = TunnelClient.BuildEmbeddingModelCommandRequest(Upstream, command, " bge-m3:latest ");
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/api/embed", request.RequestUri!.AbsoluteUri);
        Assert.Equal("bge-m3:latest", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("ReverseLlama warmup", document.RootElement.GetProperty("input").GetString());
        Assert.Equal(expectedKeepAlive, document.RootElement.GetProperty("keep_alive").GetInt32());
    }
}
