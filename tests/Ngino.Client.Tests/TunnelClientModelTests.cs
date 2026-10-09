using System.Text.Json;
using Ngino.Client;
using Xunit;

namespace Ngino.Client.Tests;

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

    [Fact]
    public async Task BuildModelCommandRequest_LoadMergesContextPayloadIntoOptions()
    {
        using var request = TunnelClient.BuildModelCommandRequest(
            Upstream,
            "load",
            "qwen3.5:0.8b",
            """{"options":{"num_ctx":8192}}""");
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/api/generate", request.RequestUri!.AbsoluteUri);
        Assert.Equal("qwen3.5:0.8b", document.RootElement.GetProperty("model").GetString());
        Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(-1, document.RootElement.GetProperty("keep_alive").GetInt32());
        Assert.Equal(8192, document.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task BuildModelCommandRequest_UnloadIgnoresContextPayload()
    {
        using var request = TunnelClient.BuildModelCommandRequest(
            Upstream,
            "unload",
            "qwen3.5:0.8b",
            """{"options":{"num_ctx":8192}}""");
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(0, document.RootElement.GetProperty("keep_alive").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("options", out _));
    }

    [Fact]
    public void ExtractModelNames_ReadsOpenAiModelList()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "object": "list",
              "data": [
                { "id": "qwen3.8-flash-next", "object": "model", "owned_by": "strata" },
                { "id": "granite4.1:8b" },
                { "id": " QWEN3.8-FLASH-NEXT " }
              ]
            }
            """);

        var models = TunnelClient.ExtractModelNames(document.RootElement);

        Assert.Equal(["granite4.1:8b", "qwen3.8-flash-next"], models);
    }

    [Fact]
    public async Task BuildOpenAiModelCommandRequest_LoadPostsModelToJsonEndpoint()
    {
        using var request = TunnelClient.BuildOpenAiModelCommandRequest(
            Upstream,
            "load",
            " qwen3.8-flash-next ",
            """{"options":{"num_ctx":8192}}""");
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/load", request.RequestUri!.AbsoluteUri);
        Assert.Equal("qwen3.8-flash-next", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(8192, document.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task BuildOpenAiModelCommandRequest_UnloadPostsToUnloadEndpoint()
    {
        using var request = TunnelClient.BuildOpenAiModelCommandRequest(Upstream, "unload", "qwen3.8-flash-next");
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/unload", request.RequestUri!.AbsoluteUri);
        Assert.Equal("qwen3.8-flash-next", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void BuildOpenAiModelCommandRequest_RejectsUnsupportedCommands()
    {
        Assert.Throws<InvalidOperationException>(
            () => TunnelClient.BuildOpenAiModelCommandRequest(Upstream, "pull", "qwen3.8-flash-next"));
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
        Assert.Equal("Ngino warmup", document.RootElement.GetProperty("input").GetString());
        Assert.Equal(expectedKeepAlive, document.RootElement.GetProperty("keep_alive").GetInt32());
    }
}
