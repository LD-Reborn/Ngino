using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Ngino.Protocol;

namespace Ngino.Server;

internal sealed class EmbeddingCache
{
    private const string JsonContentType = "application/json; charset=utf-8";

    private readonly ConcurrentDictionary<EmbeddingCacheKey, CachedEmbedding> _entries = new();
    private string _connectionString = "";
    private string _databasePath = "";
    private bool _isAvailable;
    private string? _lastError;
    private readonly ILogger<EmbeddingCache> _logger;
    private readonly SemaphoreSlim _storeLock = new(1, 1);

    public EmbeddingCache(ServerSettings settings, ILogger<EmbeddingCache> logger)
    {
        _logger = logger;

        try
        {
            _databasePath = ResolveDatabasePath(settings.EmbeddingCachePath);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString();

            Initialize();
            _isAvailable = true;
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _logger.LogError(
                exception,
                "Embedding cache is disabled because SQLite could not be initialized at {DatabasePath}.",
                string.IsNullOrWhiteSpace(_databasePath) ? settings.EmbeddingCachePath : _databasePath);
        }
    }

    public int Count => _entries.Count;

    public string DatabasePath => _databasePath;

    public bool IsAvailable => _isAvailable;

    public string? LastError => _lastError;

    public async Task<EmbeddingCacheRequest?> TryReadRequestAsync(HttpRequest request, PathString path)
    {
        if (!HttpMethods.IsPost(request.Method)
            || !TryGetEndpointKind(path, out var kind)
            || !CanHaveBody(request))
        {
            return null;
        }

        request.EnableBuffering();

        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: request.HttpContext.RequestAborted);

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadRequiredString(root, "model", out var model)
                || !TryReadInputTexts(root, kind, out var texts))
            {
                return null;
            }

            return new EmbeddingCacheRequest(kind, model, texts);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    public async Task<bool> TryWriteCachedResponseAsync(HttpContext context, EmbeddingCacheRequest request)
    {
        var embeddings = new List<CachedEmbedding>(request.Texts.Count);

        foreach (var text in request.Texts)
        {
            if (!_entries.TryGetValue(new EmbeddingCacheKey(request.Model, text), out var embedding))
            {
                return false;
            }

            embeddings.Add(embedding);
        }

        byte[] body;
        try
        {
            body = BuildResponseBody(request, embeddings);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Ignoring invalid cached embedding JSON for model {Model}.", request.Model);
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonContentType;
        context.Response.ContentLength = body.Length;
        context.Response.Headers["X-Ngino-Embedding-Cache"] = "hit";

        await context.Response.Body.WriteAsync(body, context.RequestAborted);
        return true;
    }

    public async Task StoreResponseAsync(
        EmbeddingCacheRequest request,
        TunnelMessage responseHeaders,
        byte[] body,
        CancellationToken cancellationToken)
    {
        if (!_isAvailable
            || responseHeaders.StatusCode is not >= 200 or >= 300
            || HasContentEncoding(responseHeaders)
            || body.Length == 0)
        {
            return;
        }

        List<CachedEmbedding> embeddings;
        try
        {
            embeddings = ExtractEmbeddings(request, body);
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(exception, "Embedding response for model {Model} was not cacheable JSON.", request.Model);
            return;
        }

        if (embeddings.Count != request.Texts.Count)
        {
            _logger.LogDebug(
                "Embedding response for model {Model} returned {EmbeddingCount} vector(s) for {TextCount} text(s); skipping cache store.",
                request.Model,
                embeddings.Count,
                request.Texts.Count);
            return;
        }

        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO embedding_cache (model, text, embedding_json, created_at_utc, updated_at_utc)
                VALUES ($model, $text, $embedding_json, $now, $now)
                ON CONFLICT(model, text) DO UPDATE SET
                    embedding_json = excluded.embedding_json,
                    updated_at_utc = excluded.updated_at_utc
                """;

            var modelParameter = command.Parameters.Add("$model", SqliteType.Text);
            var textParameter = command.Parameters.Add("$text", SqliteType.Text);
            var embeddingParameter = command.Parameters.Add("$embedding_json", SqliteType.Text);
            var nowParameter = command.Parameters.Add("$now", SqliteType.Text);

            var now = DateTimeOffset.UtcNow.ToString("O");
            var stored = new List<(EmbeddingCacheKey Key, CachedEmbedding Embedding)>(embeddings.Count);

            for (var index = 0; index < request.Texts.Count; index++)
            {
                var key = new EmbeddingCacheKey(request.Model, request.Texts[index]);
                var embedding = embeddings[index] with { UpdatedAtUtc = now };

                modelParameter.Value = key.Model;
                textParameter.Value = key.Text;
                embeddingParameter.Value = embedding.EmbeddingJson;
                nowParameter.Value = now;

                command.ExecuteNonQuery();
                stored.Add((key, embedding));
            }

            transaction.Commit();

            foreach (var (key, embedding) in stored)
            {
                _entries[key] = embedding;
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Failed to persist embedding cache entries to {DatabasePath}.", _databasePath);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private void Initialize()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            pragma.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS embedding_cache (
                    model TEXT NOT NULL,
                    text TEXT NOT NULL,
                    embedding_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (model, text)
                )
                """;
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT model, text, embedding_json, updated_at_utc FROM embedding_cache";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = new EmbeddingCacheKey(reader.GetString(0), reader.GetString(1));
                var embedding = new CachedEmbedding(reader.GetString(2), reader.GetString(3));
                _entries[key] = embedding;
            }
        }

        _logger.LogInformation(
            "Loaded {EmbeddingCacheCount} embedding cache entries from {DatabasePath}.",
            _entries.Count,
            _databasePath);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static bool TryGetEndpointKind(PathString path, out EmbeddingEndpointKind kind)
    {
        var value = (path.Value ?? "").TrimEnd('/');
        if (value.Length == 0)
        {
            value = "/";
        }

        if (value.Equals("/api/embed", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmbeddingEndpointKind.OllamaEmbed;
            return true;
        }

        if (value.Equals("/api/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmbeddingEndpointKind.OllamaEmbeddings;
            return true;
        }

        if (value.Equals("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmbeddingEndpointKind.OpenAi;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool CanHaveBody(HttpRequest request)
    {
        var bodyDetection = request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>();
        if (bodyDetection?.CanHaveBody is bool canHaveBody)
        {
            return canHaveBody;
        }

        return request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding");
    }

    private static bool TryReadRequiredString(JsonElement root, string propertyName, out string value)
    {
        if (!TryReadString(root, propertyName, out value))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = "";

        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return true;
    }

    private static bool TryReadInputTexts(JsonElement root, EmbeddingEndpointKind kind, out IReadOnlyList<string> texts)
    {
        texts = [];

        if (kind == EmbeddingEndpointKind.OllamaEmbeddings)
        {
            if (!TryReadString(root, "prompt", out var prompt))
            {
                return false;
            }

            texts = [prompt];
            return true;
        }

        if (!root.TryGetProperty("input", out var input))
        {
            return false;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            texts = [input.GetString() ?? ""];
            return true;
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<string>();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            values.Add(item.GetString() ?? "");
        }

        texts = values;
        return values.Count > 0;
    }

    private static bool HasContentEncoding(TunnelMessage responseHeaders) =>
        responseHeaders.Headers.Any(header => header.Name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase));

    private static List<CachedEmbedding> ExtractEmbeddings(EmbeddingCacheRequest request, byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        return request.Kind switch
        {
            EmbeddingEndpointKind.OllamaEmbeddings => ExtractOllamaEmbeddings(root),
            EmbeddingEndpointKind.OllamaEmbed => ExtractOllamaEmbed(root),
            EmbeddingEndpointKind.OpenAi => ExtractOpenAiEmbeddings(root),
            _ => []
        };
    }

    private static List<CachedEmbedding> ExtractOllamaEmbeddings(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("embedding", out var embedding)
            && embedding.ValueKind == JsonValueKind.Array)
        {
            return [new CachedEmbedding(embedding.GetRawText(), "")];
        }

        return [];
    }

    private static List<CachedEmbedding> ExtractOllamaEmbed(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("embeddings", out var embeddings)
            || embeddings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<CachedEmbedding>();
        foreach (var embedding in embeddings.EnumerateArray())
        {
            if (embedding.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            values.Add(new CachedEmbedding(embedding.GetRawText(), ""));
        }

        return values;
    }

    private static List<CachedEmbedding> ExtractOpenAiEmbeddings(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<(int Index, int Position, CachedEmbedding Embedding)>();
        var position = 0;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("embedding", out var embedding)
                || embedding.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var index = item.TryGetProperty("index", out var indexElement)
                && indexElement.ValueKind == JsonValueKind.Number
                && indexElement.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : position;

            values.Add((index, position, new CachedEmbedding(embedding.GetRawText(), "")));
            position++;
        }

        return values
            .OrderBy(value => value.Index)
            .ThenBy(value => value.Position)
            .Select(value => value.Embedding)
            .ToList();
    }

    private static byte[] BuildResponseBody(EmbeddingCacheRequest request, IReadOnlyList<CachedEmbedding> embeddings)
    {
        using var memory = new MemoryStream();
        using var writer = new Utf8JsonWriter(memory);

        writer.WriteStartObject();

        switch (request.Kind)
        {
            case EmbeddingEndpointKind.OllamaEmbeddings:
                writer.WritePropertyName("embedding");
                writer.WriteRawValue(embeddings[0].EmbeddingJson);
                break;

            case EmbeddingEndpointKind.OllamaEmbed:
                writer.WriteString("model", request.Model);
                writer.WritePropertyName("embeddings");
                WriteEmbeddingArray(writer, embeddings);
                break;

            case EmbeddingEndpointKind.OpenAi:
                writer.WriteString("object", "list");
                writer.WritePropertyName("data");
                writer.WriteStartArray();
                for (var index = 0; index < embeddings.Count; index++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("object", "embedding");
                    writer.WritePropertyName("embedding");
                    writer.WriteRawValue(embeddings[index].EmbeddingJson);
                    writer.WriteNumber("index", index);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteString("model", request.Model);
                writer.WriteStartObject("usage");
                writer.WriteNumber("prompt_tokens", 0);
                writer.WriteNumber("total_tokens", 0);
                writer.WriteEndObject();
                break;
        }

        writer.WriteEndObject();
        writer.Flush();

        return memory.ToArray();
    }

    private static void WriteEmbeddingArray(Utf8JsonWriter writer, IEnumerable<CachedEmbedding> embeddings)
    {
        writer.WriteStartArray();
        foreach (var embedding in embeddings)
        {
            writer.WriteRawValue(embedding.EmbeddingJson);
        }

        writer.WriteEndArray();
    }

    private static string ResolveDatabasePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
            return Path.IsPathRooted(expanded)
                ? expanded
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));
        }

        return Path.Combine(AppContext.BaseDirectory, "App_Data", "embedding-cache.sqlite");
    }
}

internal sealed record EmbeddingCacheRequest(
    EmbeddingEndpointKind Kind,
    string Model,
    IReadOnlyList<string> Texts);

internal enum EmbeddingEndpointKind
{
    OllamaEmbeddings,
    OllamaEmbed,
    OpenAi
}

internal readonly record struct EmbeddingCacheKey(string Model, string Text);

internal readonly record struct CachedEmbedding(string EmbeddingJson, string UpdatedAtUtc);
