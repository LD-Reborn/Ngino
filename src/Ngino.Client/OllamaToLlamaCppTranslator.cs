using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Ngino.Protocol;

namespace Ngino.Client;

internal sealed class OllamaToLlamaCppTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _modelName;
    private readonly ILogger _logger;

    public OllamaToLlamaCppTranslator(string modelName, ILogger logger)
    {
        _modelName = modelName;
        _logger = logger;
    }

    public bool TryTranslatePath(string method, string pathAndQuery, out string newPath)
    {
        var path = pathAndQuery.Split('?')[0];
        newPath = path switch
        {
            "/api/generate" => "/completion",
            "/api/chat" => "/v1/chat/completions",
            "/api/embed" or "/api/embeddings" => "/v1/embeddings",
            _ => null!
        };
        return newPath is not null;
    }

    public byte[] TranslateBody(string pathAndQuery, byte[] body)
    {
        if (body is null || body.Length == 0)
            return body;

        var path = pathAndQuery.Split('?')[0];

        try
        {
            return path switch
            {
                "/api/generate" => TranslateGenerateBody(body),
                "/api/chat" => TranslateChatBody(body),
                "/api/embed" or "/api/embeddings" => TranslateEmbedBody(path, body),
                _ => body
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to translate request body for {Path}", path);
            return body;
        }
    }

    private byte[] TranslateGenerateBody(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var result = new Dictionary<string, object?>();

        if (root.TryGetProperty("prompt", out var prompt))
            result["prompt"] = prompt.GetString() ?? "";

        result["stream"] = true;

        CopyOptions(root, result);

        if (!result.ContainsKey("n_predict"))
            result["n_predict"] = 2048;

        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    private byte[] TranslateChatBody(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var result = new Dictionary<string, object?>();

        if (root.TryGetProperty("messages", out var messages))
            result["messages"] = messages.Deserialize<object>(JsonOptions);

        result["stream"] = true;

        if (root.TryGetProperty("model", out var model))
            result["model"] = model.GetString();

        CopyOptions(root, result, chat: true);

        if (!result.ContainsKey("max_tokens"))
            result["max_tokens"] = 2048;

        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    public static bool ExtractOriginalStream(byte[] originalBody, string pathAndQuery)
    {
        var path = pathAndQuery.Split('?')[0];
        if (path is not "/api/generate" and not "/api/chat")
            return true;

        try
        {
            using var doc = JsonDocument.Parse(originalBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("stream", out var stream))
                return stream.ValueKind != JsonValueKind.False;
        }
        catch
        {
        }

        return true;
    }

    private byte[] TranslateEmbedBody(string path, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var result = new Dictionary<string, object?>
        {
            ["model"] = _modelName
        };

        if (root.TryGetProperty("input", out var input))
            result["input"] = input.Deserialize<object>(JsonOptions);
        else if (path == "/api/embeddings" && root.TryGetProperty("prompt", out var prompt))
            result["input"] = prompt.Deserialize<object>(JsonOptions);

        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    public Func<HttpResponseMessage, CancellationToken, Task> CreateResponseHandler(
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        string originalPath,
        Func<bool> originalRequestedStream) =>
        async (response, ct) =>
        {
            await TranslateAndSendResponse(response, sendAsync, requestId, originalPath, originalRequestedStream(), ct);
        };

    private async Task TranslateAndSendResponse(
        HttpResponseMessage httpResponse,
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        string originalPath,
        bool originalRequestedStream,
        CancellationToken cancellationToken)
    {
        var path = originalPath.Split('?')[0];

        switch (path)
        {
            case "/api/tags":
                await SynthesizeTagsResponse(sendAsync, requestId, cancellationToken);
                return;

            case "/api/ps":
                await SynthesizePsResponse(sendAsync, requestId, cancellationToken);
                return;
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            await ForwardRawResponse(httpResponse, sendAsync, requestId, cancellationToken);
            return;
        }

        if (path is "/api/generate" or "/api/chat")
        {
            if (originalRequestedStream)
            {
                await TranslateStreaming(path, httpResponse, sendAsync, requestId, cancellationToken);
            }
            else
            {
                await TranslateNonStreaming(path, httpResponse, sendAsync, requestId, cancellationToken);
            }
        }
        else if (path is "/api/embed" or "/api/embeddings")
        {
            await TranslateEmbeddingResponse(path, httpResponse, sendAsync, requestId, cancellationToken);
        }
        else
        {
            await ForwardRawResponse(httpResponse, sendAsync, requestId, cancellationToken);
        }
    }

    private async Task TranslateEmbeddingResponse(
        string path,
        HttpResponseMessage httpResponse,
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        CancellationToken cancellationToken)
    {
        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseHeaders,
            RequestId = requestId,
            StatusCode = (int)httpResponse.StatusCode,
            ReasonPhrase = httpResponse.ReasonPhrase
        }, cancellationToken);

        var body = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        byte[] translatedBody;

        try
        {
            translatedBody = TranslateEmbeddingResponseBody(path, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to translate embedding response for {Path}", path);
            translatedBody = body;
        }

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseBody,
            RequestId = requestId,
            Body = translatedBody
        }, cancellationToken);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseComplete,
            RequestId = requestId
        }, cancellationToken);
    }

    private byte[] TranslateEmbeddingResponseBody(string path, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var data = root.GetProperty("data");

        var embeddings = data
            .EnumerateArray()
            .Select(item => item.GetProperty("embedding").Deserialize<object>(JsonOptions))
            .ToList();

        object result = path == "/api/embeddings"
            ? new Dictionary<string, object?>
            {
                ["embedding"] = embeddings.FirstOrDefault() ?? Array.Empty<float>()
            }
            : new Dictionary<string, object?>
            {
                ["model"] = _modelName,
                ["embeddings"] = embeddings
            };

        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    private async Task ForwardRawResponse(
        HttpResponseMessage httpResponse,
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        CancellationToken cancellationToken)
    {
        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseHeaders,
            RequestId = requestId,
            StatusCode = (int)httpResponse.StatusCode,
            ReasonPhrase = httpResponse.ReasonPhrase
        }, cancellationToken);

        await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;
            await sendAsync(new TunnelMessage
            {
                Type = TunnelMessageTypes.HttpResponseBody,
                RequestId = requestId,
                Body = buffer.AsSpan(0, bytesRead).ToArray()
            }, cancellationToken);
        }

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseComplete,
            RequestId = requestId
        }, cancellationToken);
    }

    private async Task SynthesizeTagsResponse(
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        CancellationToken cancellationToken)
    {
        var modelList = new
        {
            models = new[]
            {
                new
                {
                    name = _modelName,
                    model = _modelName,
                    modified_at = DateTime.UtcNow.ToString("o"),
                    size = 0L,
                    digest = "sha256:" + _modelName,
                    details = new
                    {
                        format = "gguf",
                        family = "llama",
                        parameter_size = "",
                        quantization_level = ""
                    }
                }
            }
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(modelList, JsonOptions);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseHeaders,
            RequestId = requestId,
            StatusCode = 200,
            ReasonPhrase = "OK"
        }, cancellationToken);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseBody,
            RequestId = requestId,
            Body = body
        }, cancellationToken);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseComplete,
            RequestId = requestId
        }, cancellationToken);
    }

    private async Task SynthesizePsResponse(
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { models = Array.Empty<object>() }, JsonOptions);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseHeaders,
            RequestId = requestId,
            StatusCode = 200,
            ReasonPhrase = "OK"
        }, cancellationToken);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseBody,
            RequestId = requestId,
            Body = body
        }, cancellationToken);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseComplete,
            RequestId = requestId
        }, cancellationToken);
    }

    private async Task TranslateNonStreaming(
        string path,
        HttpResponseMessage httpResponse,
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        CancellationToken cancellationToken)
    {
        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseHeaders,
            RequestId = requestId,
            StatusCode = (int)httpResponse.StatusCode,
            ReasonPhrase = httpResponse.ReasonPhrase
        }, cancellationToken);

        var body = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        byte[] translatedBody;

        // Newer llama.cpp builds stream /completion and /v1/chat/completions as
        // SSE even though the upstream request itself asked for "stream": true.
        // Non-streaming Ollama requests must aggregate those chunks into a
        // single JSON object before translation.
        if (IsServerSentEventsBody(body))
        {
            body = AggregateServerSentEvents(path, body);
        }

        try
        {
            translatedBody = path switch
            {
                "/api/generate" => TranslateNonStreamingGenerate(body),
                "/api/chat" => TranslateNonStreamingChat(body),
                _ => body
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to translate non-streaming response");
            translatedBody = body;
        }

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseBody,
            RequestId = requestId,
            Body = translatedBody
        }, cancellationToken);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseComplete,
            RequestId = requestId
        }, cancellationToken);
    }

    private async Task TranslateStreaming(
        string path,
        HttpResponseMessage httpResponse,
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        string requestId,
        CancellationToken cancellationToken)
    {
        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseHeaders,
            RequestId = requestId,
            StatusCode = 200,
            ReasonPhrase = "OK"
        }, cancellationToken);

        await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var jsonStr = line[6..];
            if (jsonStr == "[DONE]")
                continue;

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                byte[]? chunk = path switch
                {
                    "/api/generate" => TranslateGenerateStreamChunk(root),
                    "/api/chat" => TranslateChatStreamChunk(root),
                    _ => null
                };

                if (chunk is not null)
                {
                    await sendAsync(new TunnelMessage
                    {
                        Type = TunnelMessageTypes.HttpResponseBody,
                        RequestId = requestId,
                        Body = chunk
                    }, cancellationToken);
                }
            }
            catch (JsonException)
            {
            }
        }

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseBody,
            RequestId = requestId,
            Body = Encoding.UTF8.GetBytes(
                $"{{\"model\":\"{EscapeJson(_modelName)}\",\"response\":\"\",\"done\":true}}\n")
        }, cancellationToken);

        await sendAsync(new TunnelMessage
        {
            Type = TunnelMessageTypes.HttpResponseComplete,
            RequestId = requestId
        }, cancellationToken);
    }

    private static bool IsServerSentEventsBody(byte[] body)
    {
        if (body.Length < 6)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 16));
        return prefix.TrimStart().StartsWith("data:", StringComparison.Ordinal);
    }

    private static byte[] AggregateServerSentEvents(string path, byte[] body)
    {
        var content = new StringBuilder();
        JsonObject? last = null;
        JsonObject? usage = null;

        foreach (var rawLine in Encoding.UTF8.GetString(body).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line["data:".Length..].Trim();
            if (json.Length == 0 || json == "[DONE]")
            {
                continue;
            }

            if (JsonNode.Parse(json) is not JsonObject chunk)
            {
                continue;
            }

            last = chunk;

            if (chunk["usage"] is JsonObject chunkUsage && chunkUsage.Count > 0)
            {
                usage = chunkUsage;
            }

            if (path == "/api/generate")
            {
                content.Append(chunk["content"]?.GetValue<string>() ?? "");
                continue;
            }

            if (chunk["choices"] is not JsonArray choices
                || choices.Count == 0
                || choices[0] is not JsonObject choice
                || choice["delta"] is not JsonObject delta)
            {
                continue;
            }

            content.Append(delta["content"]?.GetValue<string>() ?? "");
        }

        if (last is null)
        {
            return body;
        }

        if (path != "/api/generate")
        {
            // Chat chunks carry deltas; rebuild the single OpenAI-style object
            // that TranslateNonStreamingChat expects.
            var aggregated = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content.ToString()
                    }
                })
            };

            if (usage is not null)
            {
                aggregated["usage"] = usage.DeepClone();
            }

            return JsonSerializer.SerializeToUtf8Bytes(aggregated, JsonOptions);
        }

        // The final completion chunk already carries "stop" and "timings";
        // only the concatenated text needs to be filled in.
        last["content"] = content.ToString();
        return JsonSerializer.SerializeToUtf8Bytes(last, JsonOptions);
    }

    private byte[] TranslateNonStreamingGenerate(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var result = new Dictionary<string, object?>
        {
            ["model"] = _modelName,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["response"] = root.TryGetProperty("content", out var content) ? content.GetString() : "",
            ["done"] = root.TryGetProperty("stop", out var stop) && stop.GetBoolean()
        };

        CopyTimings(root, result);
        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    private byte[] TranslateNonStreamingChat(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var message = root.GetProperty("choices")[0].GetProperty("message");
        var content = message.GetProperty("content").GetString() ?? "";

        var result = new Dictionary<string, object?>
        {
            ["model"] = _modelName,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = content
            },
            ["done"] = true
        };

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("completion_tokens", out var comp))
                result["eval_count"] = comp.GetInt32();
            if (usage.TryGetProperty("prompt_tokens", out var prompt))
                result["prompt_eval_count"] = prompt.GetInt32();
        }

        CopyTimings(root, result);
        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    private byte[]? TranslateGenerateStreamChunk(JsonElement root)
    {
        var done = root.TryGetProperty("stop", out var stop) && stop.GetBoolean();
        var text = root.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";

        var result = new Dictionary<string, object?>
        {
            ["model"] = _modelName,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["response"] = text,
            ["done"] = done
        };

        if (done)
            CopyTimings(root, result);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        var withNewline = new byte[bytes.Length + 1];
        bytes.CopyTo(withNewline, 0);
        withNewline[^1] = (byte)'\n';
        return withNewline;
    }

    private byte[]? TranslateChatStreamChunk(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];
        var delta = choice.GetProperty("delta");
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        var done = finishReason is not null && finishReason != "null" && finishReason != "";

        var content = delta.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var role = delta.TryGetProperty("role", out var r) ? r.GetString() : null;

        var result = new Dictionary<string, object?>
        {
            ["model"] = _modelName,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = role ?? "assistant",
                ["content"] = content
            },
            ["done"] = done
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        var withNewline = new byte[bytes.Length + 1];
        bytes.CopyTo(withNewline, 0);
        withNewline[^1] = (byte)'\n';
        return withNewline;
    }

    private static void CopyOptions(JsonElement root, Dictionary<string, object?> target, bool chat = false)
    {
        if (!root.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var opt in options.EnumerateObject())
        {
            var key = (chat ? MapChatOptionName(opt.Name) : MapGenerateOptionName(opt.Name)) ?? opt.Name;
            target[key] = ValueToObject(opt.Value);
        }
    }

    private static string? MapGenerateOptionName(string name) => name switch
    {
        "num_predict" => "n_predict",
        "temperature" => "temperature",
        "top_p" => "top_p",
        "top_k" => "top_k",
        "seed" => "seed",
        "stop" => "stop",
        "repeat_penalty" => "repeat_penalty",
        "repeat_last_n" => "repeat_last_n",
        "frequency_penalty" => "frequency_penalty",
        "presence_penalty" => "presence_penalty",
        "mirostat" => "mirostat",
        "mirostat_tau" => "mirostat_tau",
        "mirostat_eta" => "mirostat_eta",
        "num_ctx" => "n_ctx",
        "num_batch" => "n_batch",
        _ => null
    };

    private static string? MapChatOptionName(string name) => name switch
    {
        "num_predict" => "max_tokens",
        "temperature" => "temperature",
        "top_p" => "top_p",
        "seed" => "seed",
        "stop" => "stop",
        "frequency_penalty" => "frequency_penalty",
        "presence_penalty" => "presence_penalty",
        _ => null
    };

    private static void CopyTimings(JsonElement root, Dictionary<string, object?> target)
    {
        if (!root.TryGetProperty("timings", out var timings))
            return;

        if (timings.TryGetProperty("predicted_n", out var predN))
            target["eval_count"] = predN.GetInt32();
        if (timings.TryGetProperty("predicted_ms", out var predMs))
            target["eval_duration"] = (long)(predMs.GetDouble() * 1_000_000);
        if (timings.TryGetProperty("prompt_n", out var promptN))
            target["prompt_eval_count"] = promptN.GetInt32();
        if (timings.TryGetProperty("prompt_ms", out var promptMs))
            target["prompt_eval_duration"] = (long)(promptMs.GetDouble() * 1_000_000);
    }

    private static object? ValueToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Deserialize<object>(JsonOptions)
        };
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
