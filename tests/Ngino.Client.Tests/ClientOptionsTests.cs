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

    [Fact]
    public void LlamaCppNumCtxIsParsedFromArgument()
    {
        var options = ClientOptions.Parse(["--llama-cpp-num-ctx", "262144"]);

        Assert.Equal(262144, options.LlamaCppNumCtx);
    }

    [Fact]
    public void LlamaCppNumCtxIsParsedFromEnvironmentStyleValue()
    {
        var options = ClientOptions.Parse(["--llama-cpp-num-ctx=524288"]);

        Assert.Equal(524288, options.LlamaCppNumCtx);
    }

    [Fact]
    public void LlamaCppNumCtxIsNullByDefault()
    {
        var options = ClientOptions.Parse([]);

        Assert.Null(options.LlamaCppNumCtx);
    }

    [Fact]
    public void LlamaCppNumCtxIgnoresNonPositiveValues()
    {
        var options = ClientOptions.Parse(["--llama-cpp-num-ctx=0"]);

        Assert.Null(options.LlamaCppNumCtx);
    }

    [Fact]
    public void LlamaCppKvUnifiedIsDisabledByDefault()
    {
        var options = ClientOptions.Parse([]);

        Assert.False(options.LlamaCppKvUnified);
    }

    [Fact]
    public void LlamaCppKvUnifiedCanBeEnabledWithFlag()
    {
        var options = ClientOptions.Parse(["--llama-cpp-kv-unified"]);

        Assert.True(options.LlamaCppKvUnified);
    }

    [Fact]
    public void LlamaCppKvUnifiedCanBeEnabledWithEnvironmentStyleValue()
    {
        var options = ClientOptions.Parse(["--llama-cpp-kv-unified=true"]);

        Assert.True(options.LlamaCppKvUnified);
    }

    [Fact]
    public void LlamaCppKvUnifiedCanBeDisabledWithExplicitFalse()
    {
        var options = ClientOptions.Parse(["--llama-cpp-kv-unified=false"]);

        Assert.False(options.LlamaCppKvUnified);
    }

    [Fact]
    public void UpstreamModeDefaultsToOllama()
    {
        var options = ClientOptions.Parse([]);

        Assert.Equal(UpstreamMode.Ollama, options.UpstreamMode);
    }

    [Fact]
    public void UpstreamModeCanBeSetToOpenAi()
    {
        var options = ClientOptions.Parse(["--upstream-mode", "openai"]);

        Assert.Equal(UpstreamMode.OpenAi, options.UpstreamMode);
    }

    [Fact]
    public void UpstreamModeAcceptsEnvironmentStyleValue()
    {
        var options = ClientOptions.Parse(["--upstream-mode=openai"]);

        Assert.Equal(UpstreamMode.OpenAi, options.UpstreamMode);
    }

    [Fact]
    public void UpstreamModeRejectsUnknownValues()
    {
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse(["--upstream-mode=bogus"]));
    }
}
