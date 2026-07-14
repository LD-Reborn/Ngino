using System.Text;
using System.Text.Json;

namespace ReverseLlama.Server;

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

    public int CountTokens()
    {
        if (_buffer.Length == 0)
        {
            return 0;
        }

        var payload = Encoding.UTF8.GetString(_buffer.ToArray());
        var total = 0;
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

            if (TryExtractFromJson(line, out var lineTokens))
            {
                parsedLines = true;
                total += lineTokens;
            }
        }

        if (parsedLines)
        {
            return total;
        }

        return TryExtractFromJson(payload, out var tokens) ? tokens : 0;
    }

    private static bool TryExtractFromJson(string json, out int tokens)
    {
        tokens = 0;

        try
        {
            using var document = JsonDocument.Parse(json);
            tokens = ExtractTokens(document.RootElement);
            return tokens > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ExtractTokens(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var total = 0;
            foreach (var item in element.EnumerateArray())
            {
                total += ExtractTokens(item);
            }

            return total;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (element.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (TryGetInt(usage, "total_tokens", out var totalTokens))
            {
                return totalTokens;
            }

            var usageTotal = 0;
            if (TryGetInt(usage, "prompt_tokens", out var promptTokens))
            {
                usageTotal += promptTokens;
            }

            if (TryGetInt(usage, "completion_tokens", out var completionTokens))
            {
                usageTotal += completionTokens;
            }

            if (TryGetInt(usage, "input_tokens", out var inputTokens))
            {
                usageTotal += inputTokens;
            }

            if (TryGetInt(usage, "output_tokens", out var outputTokens))
            {
                usageTotal += outputTokens;
            }

            if (usageTotal > 0)
            {
                return usageTotal;
            }
        }

        var ollamaTotal = 0;
        if (TryGetInt(element, "prompt_eval_count", out var promptEvalCount))
        {
            ollamaTotal += promptEvalCount;
        }

        if (TryGetInt(element, "eval_count", out var evalCount))
        {
            ollamaTotal += evalCount;
        }

        return ollamaTotal;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}
