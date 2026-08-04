using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Ngino.Client;
using Ngino.Protocol;
using Xunit;

namespace Ngino.Client.Tests;

public sealed class OllamaToLlamaCppTranslatorTests
{
    private readonly OllamaToLlamaCppTranslator _translator =
        new("bge-m3:latest", NullLogger.Instance);

    [Fact]
    public void TranslateBody_LegacyEmbeddingsMapsPromptToInput()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            { "model": "bge-m3:latest", "prompt": "hello" }
            """);

        var translated = _translator.TranslateBody("/api/embeddings", body);
        using var document = JsonDocument.Parse(translated);

        Assert.Equal("bge-m3:latest", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", document.RootElement.GetProperty("input").GetString());
        Assert.False(document.RootElement.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task ResponseHandler_LegacyEmbeddingsReturnsOllamaShape()
    {
        var messages = await TranslateResponseAsync("/api/embeddings");
        using var document = JsonDocument.Parse(GetResponseBody(messages));

        Assert.Equal(0.25, document.RootElement.GetProperty("embedding")[0].GetDouble());
        Assert.Equal(0.75, document.RootElement.GetProperty("embedding")[1].GetDouble());
        Assert.False(document.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task ResponseHandler_CurrentEmbedReturnsOllamaShape()
    {
        var messages = await TranslateResponseAsync("/api/embed");
        using var document = JsonDocument.Parse(GetResponseBody(messages));

        Assert.Equal("bge-m3:latest", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.25, document.RootElement.GetProperty("embeddings")[0][0].GetDouble());
        Assert.Equal(0.75, document.RootElement.GetProperty("embeddings")[0][1].GetDouble());
        Assert.False(document.RootElement.TryGetProperty("data", out _));
    }

    private async Task<List<TunnelMessage>> TranslateResponseAsync(string path)
    {
        var messages = new List<TunnelMessage>();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                { "object": "list", "data": [{ "object": "embedding", "embedding": [0.25, 0.75], "index": 0 }] }
                """,
                Encoding.UTF8,
                "application/json")
        };

        var handler = _translator.CreateResponseHandler(
            (message, _) =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            },
            "request-1",
            path,
            () => false);

        await handler(response, CancellationToken.None);
        return messages;
    }

    private static byte[] GetResponseBody(IEnumerable<TunnelMessage> messages) =>
        messages
            .Where(message => message.Type == TunnelMessageTypes.HttpResponseBody)
            .SelectMany(message => message.Body ?? [])
            .ToArray();
}
