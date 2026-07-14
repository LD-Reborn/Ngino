using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReverseLlama.Server;

internal sealed class ManagementStore
{
    private static readonly TimeSpan ApiKeyLastUsedWriteInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, ApiKeyState> _apiKeysByHash = new(StringComparer.Ordinal);
    private readonly string _connectionString = "";
    private readonly string _databasePath = "";
    private readonly object _lock = new();
    private readonly ILogger<ManagementStore> _logger;
    private bool _isAvailable;
    private string? _lastError;

    public ManagementStore(ServerSettings settings, ILogger<ManagementStore> logger)
    {
        _logger = logger;

        try
        {
            _databasePath = ResolveDatabasePath(settings.ManagementDatabasePath);
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
                "Management database is disabled because SQLite could not be initialized at {DatabasePath}.",
                string.IsNullOrWhiteSpace(_databasePath) ? settings.ManagementDatabasePath : _databasePath);
        }
    }

    public string DatabasePath => _databasePath;

    public bool IsAvailable => _isAvailable;

    public string? LastError => _lastError;

    public bool HasApiKeys
    {
        get
        {
            if (!_isAvailable)
            {
                return false;
            }

            lock (_lock)
            {
                return _apiKeysByHash.Count > 0;
            }
        }
    }

    public bool IsApiKeyValid(string apiKey, bool updateLastUsed)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        var hash = HashApiKey(apiKey);
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (!_apiKeysByHash.TryGetValue(hash, out var key))
            {
                return false;
            }

            if (!updateLastUsed
                || key.LastUsedUtc is not null
                && now - key.LastUsedUtc.Value < ApiKeyLastUsedWriteInterval)
            {
                return true;
            }

            key.LastUsedUtc = now;

            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE api_keys SET last_used_at_utc = $last_used_at_utc WHERE id = $id";
                command.Parameters.AddWithValue("$last_used_at_utc", now.ToString("O"));
                command.Parameters.AddWithValue("$id", key.Id);
                command.ExecuteNonQuery();
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Failed to update API key last-used timestamp.");
            }

            return true;
        }
    }

    public IReadOnlyList<ApiKeyInfo> ListApiKeys()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            return _apiKeysByHash.Values
                .OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(key => key.CreatedAtUtc)
                .Select(key => new ApiKeyInfo(
                    key.Id,
                    key.Name,
                    key.KeyPrefix,
                    key.CreatedAtUtc,
                    key.LastUsedUtc))
                .ToList();
        }
    }

    public CreatedApiKey CreateApiKey(string? name)
    {
        EnsureAvailable();

        var apiKey = GenerateApiKey();
        var now = DateTimeOffset.UtcNow;
        var state = new ApiKeyState
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = string.IsNullOrWhiteSpace(name) ? "API key" : name.Trim(),
            KeyHash = HashApiKey(apiKey),
            KeyPrefix = GetKeyPrefix(apiKey),
            CreatedAtUtc = now
        };

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO api_keys (id, name, key_hash, key_prefix, created_at_utc)
                VALUES ($id, $name, $key_hash, $key_prefix, $created_at_utc)
                """;
            command.Parameters.AddWithValue("$id", state.Id);
            command.Parameters.AddWithValue("$name", state.Name);
            command.Parameters.AddWithValue("$key_hash", state.KeyHash);
            command.Parameters.AddWithValue("$key_prefix", state.KeyPrefix);
            command.Parameters.AddWithValue("$created_at_utc", state.CreatedAtUtc.ToString("O"));
            command.ExecuteNonQuery();

            _apiKeysByHash[state.KeyHash] = state;
        }

        return new CreatedApiKey(
            state.Id,
            state.Name,
            state.KeyPrefix,
            state.CreatedAtUtc,
            apiKey);
    }

    public bool DeleteApiKey(string id)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM api_keys WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            var deleted = command.ExecuteNonQuery() > 0;

            if (deleted)
            {
                foreach (var pair in _apiKeysByHash.Where(pair => pair.Value.Id == id).ToArray())
                {
                    _apiKeysByHash.Remove(pair.Key);
                }
            }

            return deleted;
        }
    }

    public ClientAccess GetClientAccess(string clientId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(clientId))
        {
            return ClientAccess.Enabled;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT disabled_until_utc, disabled_manually, disabled_reason
                FROM client_controls
                WHERE client_id = $client_id
                """;
            command.Parameters.AddWithValue("$client_id", clientId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return ClientAccess.Enabled;
            }

            var disabledUntil = ReadNullableDateTimeOffset(reader, 0);
            var disabledManually = reader.GetInt32(1) != 0;
            var reason = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (disabledManually)
            {
                return new ClientAccess(true, null, true, reason);
            }

            if (disabledUntil is { } until && until > DateTimeOffset.UtcNow)
            {
                return new ClientAccess(true, until, false, reason);
            }

            return ClientAccess.Enabled;
        }
    }

    public IReadOnlyDictionary<string, ClientAccess> ListClientControls()
    {
        if (!_isAvailable)
        {
            return new Dictionary<string, ClientAccess>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, ClientAccess>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT client_id, disabled_until_utc, disabled_manually, disabled_reason FROM client_controls";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var clientId = reader.GetString(0);
                var disabledUntil = ReadNullableDateTimeOffset(reader, 1);
                var disabledManually = reader.GetInt32(2) != 0;
                var reason = reader.IsDBNull(3) ? null : reader.GetString(3);

                result[clientId] = disabledManually
                    ? new ClientAccess(true, null, true, reason)
                    : disabledUntil is { } until && until > DateTimeOffset.UtcNow
                        ? new ClientAccess(true, until, false, reason)
                        : ClientAccess.Enabled;
            }
        }

        return result;
    }

    public void DisableClient(string clientId, TimeSpan? duration, bool manually, string? reason)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id is required.", nameof(clientId));
        }

        var now = DateTimeOffset.UtcNow;
        var disabledUntil = manually ? null : now.Add(duration ?? TimeSpan.FromHours(1)).ToString("O");

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO client_controls (
                    client_id,
                    disabled_until_utc,
                    disabled_manually,
                    disabled_reason,
                    updated_at_utc)
                VALUES (
                    $client_id,
                    $disabled_until_utc,
                    $disabled_manually,
                    $disabled_reason,
                    $updated_at_utc)
                ON CONFLICT(client_id) DO UPDATE SET
                    disabled_until_utc = excluded.disabled_until_utc,
                    disabled_manually = excluded.disabled_manually,
                    disabled_reason = excluded.disabled_reason,
                    updated_at_utc = excluded.updated_at_utc
                """;
            command.Parameters.AddWithValue("$client_id", clientId);
            command.Parameters.AddWithValue("$disabled_until_utc", (object?)disabledUntil ?? DBNull.Value);
            command.Parameters.AddWithValue("$disabled_manually", manually ? 1 : 0);
            command.Parameters.AddWithValue("$disabled_reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason.Trim());
            command.Parameters.AddWithValue("$updated_at_utc", now.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void EnableClient(string clientId)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id is required.", nameof(clientId));
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO client_controls (
                    client_id,
                    disabled_until_utc,
                    disabled_manually,
                    disabled_reason,
                    updated_at_utc)
                VALUES (
                    $client_id,
                    NULL,
                    0,
                    NULL,
                    $updated_at_utc)
                ON CONFLICT(client_id) DO UPDATE SET
                    disabled_until_utc = NULL,
                    disabled_manually = 0,
                    disabled_reason = NULL,
                    updated_at_utc = excluded.updated_at_utc
                """;
            command.Parameters.AddWithValue("$client_id", clientId);
            command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void RecordRequest(RequestMetric metric)
    {
        if (!_isAvailable)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO request_metrics (
                        client_id,
                        model,
                        method,
                        path,
                        status_code,
                        token_count,
                        started_at_utc,
                        completed_at_utc,
                        duration_ms)
                    VALUES (
                        $client_id,
                        $model,
                        $method,
                        $path,
                        $status_code,
                        $token_count,
                        $started_at_utc,
                        $completed_at_utc,
                        $duration_ms)
                    """;
                command.Parameters.AddWithValue("$client_id", metric.ClientId);
                command.Parameters.AddWithValue("$model", string.IsNullOrWhiteSpace(metric.Model) ? DBNull.Value : metric.Model);
                command.Parameters.AddWithValue("$method", metric.Method);
                command.Parameters.AddWithValue("$path", metric.Path);
                command.Parameters.AddWithValue("$status_code", metric.StatusCode is null ? DBNull.Value : metric.StatusCode.Value);
                command.Parameters.AddWithValue("$token_count", metric.TokenCount);
                command.Parameters.AddWithValue("$started_at_utc", metric.StartedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$completed_at_utc", metric.CompletedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$duration_ms", metric.Duration.TotalMilliseconds);
                command.ExecuteNonQuery();
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Failed to record request metric for client {ClientId}.", metric.ClientId);
        }
    }

    public IReadOnlyDictionary<string, ClientRequestStats> GetClientRequestStats()
    {
        if (!_isAvailable)
        {
            return new Dictionary<string, ClientRequestStats>(StringComparer.OrdinalIgnoreCase);
        }

        var now = DateTimeOffset.UtcNow;
        var since10 = now.AddMinutes(-10).ToString("O");
        var sinceHour = now.AddHours(-1).ToString("O");
        var result = new Dictionary<string, ClientRequestStats>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    client_id,
                    COUNT(*),
                    SUM(CASE WHEN started_at_utc >= $since10 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN started_at_utc >= $sinceHour THEN 1 ELSE 0 END)
                FROM request_metrics
                GROUP BY client_id
                """;
            command.Parameters.AddWithValue("$since10", since10);
            command.Parameters.AddWithValue("$sinceHour", sinceHour);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = new ClientRequestStats(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3));
            }
        }

        return result;
    }

    public IReadOnlyDictionary<string, ModelUsageStats> GetModelUsageStats()
    {
        if (!_isAvailable)
        {
            return new Dictionary<string, ModelUsageStats>(StringComparer.OrdinalIgnoreCase);
        }

        var now = DateTimeOffset.UtcNow;
        var since10 = now.AddMinutes(-10).ToString("O");
        var sinceHour = now.AddHours(-1).ToString("O");
        var result = new Dictionary<string, ModelUsageStats>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    model,
                    COUNT(*),
                    SUM(CASE WHEN started_at_utc >= $since10 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN started_at_utc >= $sinceHour THEN 1 ELSE 0 END),
                    SUM(CASE WHEN started_at_utc >= $since10 THEN token_count ELSE 0 END),
                    SUM(CASE WHEN started_at_utc >= $sinceHour THEN token_count ELSE 0 END)
                FROM request_metrics
                WHERE model IS NOT NULL AND model <> ''
                GROUP BY model
                """;
            command.Parameters.AddWithValue("$since10", since10);
            command.Parameters.AddWithValue("$sinceHour", sinceHour);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = new ModelUsageStats(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5));
            }
        }

        return result;
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
                CREATE TABLE IF NOT EXISTS client_controls (
                    client_id TEXT NOT NULL PRIMARY KEY,
                    disabled_until_utc TEXT NULL,
                    disabled_manually INTEGER NOT NULL DEFAULT 0,
                    disabled_reason TEXT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS api_keys (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    key_hash TEXT NOT NULL UNIQUE,
                    key_prefix TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    last_used_at_utc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS request_metrics (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    client_id TEXT NOT NULL,
                    model TEXT NULL,
                    method TEXT NOT NULL,
                    path TEXT NOT NULL,
                    status_code INTEGER NULL,
                    token_count INTEGER NOT NULL DEFAULT 0,
                    started_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT NOT NULL,
                    duration_ms REAL NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_request_metrics_client_started
                    ON request_metrics (client_id, started_at_utc);

                CREATE INDEX IF NOT EXISTS idx_request_metrics_model_started
                    ON request_metrics (model, started_at_utc);
                """;
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, key_hash, key_prefix, created_at_utc, last_used_at_utc
                FROM api_keys
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var state = new ApiKeyState
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    KeyHash = reader.GetString(2),
                    KeyPrefix = reader.GetString(3),
                    CreatedAtUtc = ReadDateTimeOffset(reader.GetString(4)),
                    LastUsedUtc = ReadNullableDateTimeOffset(reader, 5)
                };

                _apiKeysByHash[state.KeyHash] = state;
            }
        }

        _logger.LogInformation(
            "Loaded {ApiKeyCount} API key(s) from {DatabasePath}.",
            _apiKeysByHash.Count,
            _databasePath);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureAvailable()
    {
        if (!_isAvailable)
        {
            throw new InvalidOperationException(_lastError ?? "The management database is not available.");
        }
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadDateTimeOffset(reader.GetString(ordinal));

    private static DateTimeOffset ReadDateTimeOffset(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"rl_{Base64UrlEncode(bytes)}";
    }

    private static string HashApiKey(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();

    private static string GetKeyPrefix(string apiKey) =>
        apiKey.Length <= 12 ? apiKey : apiKey[..12];

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string ResolveDatabasePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
            return Path.IsPathRooted(expanded)
                ? expanded
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));
        }

        return Path.Combine(AppContext.BaseDirectory, "App_Data", "management.sqlite");
    }

    private sealed class ApiKeyState
    {
        public string Id { get; init; } = "";

        public string Name { get; init; } = "";

        public string KeyHash { get; init; } = "";

        public string KeyPrefix { get; init; } = "";

        public DateTimeOffset CreatedAtUtc { get; init; }

        public DateTimeOffset? LastUsedUtc { get; set; }
    }
}

internal sealed record ClientAccess(
    bool IsDisabled,
    DateTimeOffset? DisabledUntilUtc,
    bool DisabledManually,
    string? DisabledReason)
{
    public static ClientAccess Enabled { get; } = new(false, null, false, null);
}

internal sealed record ApiKeyInfo(
    string Id,
    string Name,
    string KeyPrefix,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastUsedUtc);

internal sealed record CreatedApiKey(
    string Id,
    string Name,
    string KeyPrefix,
    DateTimeOffset CreatedAtUtc,
    string Key);

internal sealed record RequestMetric(
    string ClientId,
    string? Model,
    string Method,
    string Path,
    int? StatusCode,
    int TokenCount,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan Duration);

internal sealed record ClientRequestStats(
    long Total,
    long Last10Minutes,
    long LastHour);

internal sealed record ModelUsageStats(
    long TotalRequests,
    long RequestsLast10Minutes,
    long RequestsLastHour,
    long TokensLast10Minutes,
    long TokensLastHour);
