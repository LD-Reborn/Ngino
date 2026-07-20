using System.Text;
using System.Text.Json;

namespace Ngino.Server;

internal sealed class ResponseTokenCounter
{
    private const int MaxBufferedBytes = 4 * 1024 * 1024;

    private readonly MemoryStream _buffer = new();

    public void Add(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0 || _buffer.Length >= MaxBufferedBytes)
        {
            return;
        }

        var available = MaxBufferedBytes - (int)_buffer.Length;
        var length = Math.Min(chunk.Length, available);
        _buffer.Write(chunk[..length]);
    }

    public TokenCounts CountTokens()
    {
        if (_buffer.Length == 0)
        {
            return new TokenCounts(0, 0, 0);
        }

        var payload = Encoding.UTF8.GetString(_buffer.ToArray());
        var totalPrompt = 0;
        var totalCompletion = 0;
        var parsedLines = false;

        foreach (var rawLine in payload.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                line = line["data:".Length..].Trim();
            }

            if (line.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryExtractTokenCountsFromJson(line, out var prompt, out var completion))
            {
                parsedLines = true;
                totalPrompt += prompt;
                totalCompletion += completion;
            }
        }

        if (parsedLines)
        {
            return new TokenCounts(totalPrompt, totalCompletion, totalPrompt + totalCompletion);
        }

        if (TryExtractTokenCountsFromJson(payload, out var promptFallback, out var completionFallback))
        {
            return new TokenCounts(promptFallback, completionFallback, promptFallback + completionFallback);
        }

        return new TokenCounts(0, 0, 0);
    }

    private static bool TryExtractTokenCountsFromJson(string json, out int promptTokens, out int completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;

        try
        {
            using var document = JsonDocument.Parse(json);
            ExtractTokenCounts(document.RootElement, out promptTokens, out completionTokens);
            return promptTokens > 0 || completionTokens > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ExtractTokenCounts(JsonElement element, out int promptTokens, out int completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractTokenCounts(item, out var itemPrompt, out var itemCompletion);
                promptTokens += itemPrompt;
                completionTokens += itemCompletion;
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (TryGetInt(usage, "prompt_tokens", out var pt))
            {
                promptTokens += pt;
            }

            if (TryGetInt(usage, "completion_tokens", out var ct))
            {
                completionTokens += ct;
            }

            if (promptTokens == 0 && completionTokens == 0)
            {
                if (TryGetInt(usage, "input_tokens", out var it))
                {
                    promptTokens += it;
                }

                if (TryGetInt(usage, "output_tokens", out var ot))
                {
                    completionTokens += ot;
                }
            }

            if (promptTokens > 0 || completionTokens > 0)
            {
                return;
            }
        }

        if (TryGetInt(element, "prompt_eval_count", out var promptEval))
        {
            promptTokens += promptEval;
        }

        if (TryGetInt(element, "eval_count", out var eval))
        {
            completionTokens += eval;
        }
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}

internal sealed record TokenCounts(int PromptTokens, int CompletionTokens, int TotalTokens);
