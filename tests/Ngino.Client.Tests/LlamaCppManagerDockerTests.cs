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
        var model = new LlamaCppModel
        {
            OllamaName = "granite4.1:8b",
            BlobDigest = "sha256:test",
            BlobPath = Path.GetFullPath(Path.Combine("models", "blobs", "sha256-test")),
            ManifestPath = Path.GetFullPath(Path.Combine("models", "manifests", "granite4.1", "8b"))
        };

        var args = manager.BuildDockerRunArgs("ngino-llamacpp-granite4.1_8b", model, 8081);

        Assert.Contains("--gpus=all", args);
        Assert.Contains("ghcr.io/ggml-org/llama.cpp:server-cuda", args);
        Assert.Equal("4", args[Array.IndexOf(args, "--parallel") + 1]);
    }
}
