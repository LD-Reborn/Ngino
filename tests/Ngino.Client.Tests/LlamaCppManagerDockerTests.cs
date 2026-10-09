using Microsoft.Extensions.Logging.Abstractions;
using Ngino.Client;
using Xunit;

namespace Ngino.Client.Tests;

public sealed class LlamaCppManagerDockerTests
{
    [Fact]
    public void BuildDockerRunArgs_ExplicitCudaImageRequestsAllGpus()
    {
        var manager = new LlamaCppManager(
            "models",
            "ghcr.io/ggml-org/llama.cpp:server-cuda",
            8081,
            NullLogger.Instance,
            parallel: 4);

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", CreateModel(), 8081);

        Assert.Contains("--gpus=all", args);
        Assert.Contains("ghcr.io/ggml-org/llama.cpp:server-cuda", args);
        Assert.Equal("4", args[Array.IndexOf(args, "--parallel") + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_NumCtxWithParallelEmitsTotalContextSize()
    {
        var manager = new LlamaCppManager(
            "models",
            "ghcr.io/ggml-org/llama.cpp:server-cuda",
            8081,
            NullLogger.Instance,
            parallel: 2,
            numCtx: 262144);

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", CreateModel(), 8081);

        Assert.Equal("2", args[Array.IndexOf(args, "--parallel") + 1]);
        Assert.Equal("524288", args[Array.IndexOf(args, "--ctx-size") + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_NumCtxWithoutParallelEmitsNumCtx()
    {
        var manager = new LlamaCppManager(
            "models",
            "ghcr.io/ggml-org/llama.cpp:server-cuda",
            8081,
            NullLogger.Instance,
            numCtx: 131072);

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", CreateModel(), 8081);

        Assert.DoesNotContain("--parallel", args);
        Assert.Equal("131072", args[Array.IndexOf(args, "--ctx-size") + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_PerCallContextLengthOverridesOption()
    {
        var manager = new LlamaCppManager(
            "models",
            "ghcr.io/ggml-org/llama.cpp:server-cuda",
            8081,
            NullLogger.Instance,
            parallel: 2,
            numCtx: 262144);

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", CreateModel(), 8081, contextLength: 4096);

        Assert.Equal("2", args[Array.IndexOf(args, "--parallel") + 1]);
        Assert.Equal("8192", args[Array.IndexOf(args, "--ctx-size") + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_WithoutNumCtxOmitsContextSize()
    {
        var manager = new LlamaCppManager(
            "models",
            "ghcr.io/ggml-org/llama.cpp:server-cuda",
            8081,
            NullLogger.Instance);

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", CreateModel(), 8081);

        Assert.DoesNotContain("--ctx-size", args);
    }

    [Fact]
    public void BuildDockerRunArgs_KvUnifiedEmitsFlag()
    {
        var manager = new LlamaCppManager(
            "models",
            "ghcr.io/ggml-org/llama.cpp:server-cuda",
            8081,
            NullLogger.Instance,
            kvUnified: true);

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", CreateModel(), 8081);

        Assert.Contains("--kv-unified", args);
    }

    [Fact]
    public void BuildDockerRunArgs_KvUnifiedOffByDefault()
    {
        var manager = new LlamaCppManager(
            "models",
            "ghcr.io/ggml-org/llama.cpp:server-cuda",
            8081,
            NullLogger.Instance);

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", CreateModel(), 8081);

        Assert.DoesNotContain("--kv-unified", args);
    }

    private static LlamaCppModel CreateModel() => new()
    {
        OllamaName = "granite4.1:8b",
        BlobDigest = "sha256:test",
        BlobPath = Path.GetFullPath(Path.Combine("models", "blobs", "sha256-test")),
        ManifestPath = Path.GetFullPath(Path.Combine("models", "manifests", "granite4.1", "8b"))
    };
}
