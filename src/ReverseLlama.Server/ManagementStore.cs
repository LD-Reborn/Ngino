using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
                        prompt_tokens,
                        completion_tokens,
                        token_count,
                        api_key_id,
                        cost,
                        started_at_utc,
                        completed_at_utc,
                        duration_ms)
                    VALUES (
                        $client_id,
                        $model,
                        $method,
                        $path,
                        $status_code,
                        $prompt_tokens,
                        $completion_tokens,
                        $token_count,
                        $api_key_id,
                        $cost,
                        $started_at_utc,
                        $completed_at_utc,
                        $duration_ms)
                    """;
                command.Parameters.AddWithValue("$client_id", metric.ClientId);
                command.Parameters.AddWithValue("$model", string.IsNullOrWhiteSpace(metric.Model) ? DBNull.Value : metric.Model);
                command.Parameters.AddWithValue("$method", metric.Method);
                command.Parameters.AddWithValue("$path", metric.Path);
                command.Parameters.AddWithValue("$status_code", metric.StatusCode is null ? DBNull.Value : metric.StatusCode.Value);
                command.Parameters.AddWithValue("$prompt_tokens", metric.PromptTokens);
                command.Parameters.AddWithValue("$completion_tokens", metric.CompletionTokens);
                command.Parameters.AddWithValue("$token_count", metric.TokenCount);
                command.Parameters.AddWithValue("$api_key_id", string.IsNullOrWhiteSpace(metric.ApiKeyId) ? DBNull.Value : metric.ApiKeyId);
                command.Parameters.AddWithValue("$cost", metric.Cost);
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

    public string? GetApiKeyId(string apiKey)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var hash = HashApiKey(apiKey);

        lock (_lock)
        {
            return _apiKeysByHash.TryGetValue(hash, out var key) ? key.Id : null;
        }
    }

    public IReadOnlyList<GroupInfo> ListGroups()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, created_at_utc, updated_at_utc FROM groups ORDER BY name";

            var result = new List<GroupInfo>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new GroupInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    ReadDateTimeOffset(reader.GetString(2)),
                    ReadDateTimeOffset(reader.GetString(3))));
            }

            return result;
        }
    }

    public GroupInfo? GetGroup(string groupId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, created_at_utc, updated_at_utc FROM groups WHERE id = $id";
            command.Parameters.AddWithValue("$id", groupId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new GroupInfo(
                reader.GetString(0),
                reader.GetString(1),
                ReadDateTimeOffset(reader.GetString(2)),
                ReadDateTimeOffset(reader.GetString(3)));
        }
    }

    public GroupInfo CreateGroup(string? name)
    {
        EnsureAvailable();

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("n");
        var groupName = string.IsNullOrWhiteSpace(name) ? "Group" : name.Trim();

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO groups (id, name, created_at_utc, updated_at_utc)
                VALUES ($id, $name, $created_at_utc, $updated_at_utc)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", groupName);
            command.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            command.Parameters.AddWithValue("$updated_at_utc", now.ToString("O"));
            command.ExecuteNonQuery();
        }

        return new GroupInfo(id, groupName, now, now);
    }

    public bool UpdateGroup(string groupId, string name)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE groups SET name = $name, updated_at_utc = $updated_at_utc
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", groupId);
            command.Parameters.AddWithValue("$name", name.Trim());
            command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O"));
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteGroup(string groupId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId))
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM groups WHERE id = $id";
            command.Parameters.AddWithValue("$id", groupId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<GroupClientInfo> ListGroupClients(string groupId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId))
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, group_id, client_id, model, client_pattern
                FROM group_members
                WHERE group_id = $group_id
                ORDER BY client_id, model, client_pattern
                """;
            command.Parameters.AddWithValue("$group_id", groupId);

            var result = new List<GroupClientInfo>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new GroupClientInfo(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return result;
        }
    }

    public GroupClientInfo AddGroupClient(string groupId, string? clientId, string? model, string? clientPattern)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ArgumentException("Group id is required.", nameof(groupId));
        }

        if (string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(clientPattern))
        {
            throw new ArgumentException("Either client_id or client_pattern is required.");
        }

        if (!string.IsNullOrWhiteSpace(clientPattern))
        {
            try
            {
                _ = Regex.IsMatch("", clientPattern);
            }
            catch (RegexParseException ex)
            {
                throw new ArgumentException($"Unable to add client - invalid regex: {ex.Message}", nameof(clientPattern));
            }
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO group_members (group_id, client_id, model, client_pattern)
                VALUES ($group_id, $client_id, $model, $client_pattern)
                """;
            command.Parameters.AddWithValue("$group_id", groupId);
            command.Parameters.AddWithValue("$client_id", string.IsNullOrWhiteSpace(clientId) ? DBNull.Value : clientId);
            command.Parameters.AddWithValue("$model", string.IsNullOrWhiteSpace(model) ? DBNull.Value : model);
            command.Parameters.AddWithValue("$client_pattern", string.IsNullOrWhiteSpace(clientPattern) ? DBNull.Value : clientPattern);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            var insertedId = (long)idCommand.ExecuteScalar()!;
            return new GroupClientInfo(insertedId, groupId, clientId, model, clientPattern);
        }
    }

    public bool RemoveGroupClient(long memberId)
    {
        if (!_isAvailable)
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM group_members WHERE id = $id";
            command.Parameters.AddWithValue("$id", memberId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<string> GetApiKeyGroupIds(string apiKeyId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(apiKeyId))
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT g.id FROM groups g
                INNER JOIN api_key_groups akg ON g.id = akg.group_id
                WHERE akg.api_key_id = $api_key_id
                ORDER BY g.name
                """;
            command.Parameters.AddWithValue("$api_key_id", apiKeyId);

            var result = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
    }

    public void SetApiKeyGroups(string apiKeyId, IReadOnlyList<string> groupIds)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(apiKeyId))
        {
            throw new ArgumentException("API key id is required.", nameof(apiKeyId));
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                using (var deleteCommand = connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = "DELETE FROM api_key_groups WHERE api_key_id = $api_key_id";
                    deleteCommand.Parameters.AddWithValue("$api_key_id", apiKeyId);
                    deleteCommand.ExecuteNonQuery();
                }

                using (var insertCommand = connection.CreateCommand())
                {
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = """
                        INSERT INTO api_key_groups (api_key_id, group_id)
                        VALUES ($api_key_id, $group_id)
                        """;

                    var apiKeyParam = insertCommand.Parameters.Add("$api_key_id", SqliteType.Text);
                    var groupParam = insertCommand.Parameters.Add("$group_id", SqliteType.Text);
                    apiKeyParam.Value = apiKeyId;

                    foreach (var groupId in groupIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        groupParam.Value = groupId;
                        insertCommand.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public IReadOnlyList<ApiKeyGroupInfo> ListApiKeyGroups()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ak.id, ak.name, ak.key_prefix,
                    GROUP_CONCAT(g.id) as group_ids,
                    GROUP_CONCAT(g.name) as group_names
                FROM api_keys ak
                LEFT JOIN api_key_groups akg ON ak.id = akg.api_key_id
                LEFT JOIN groups g ON akg.group_id = g.id
                GROUP BY ak.id
                ORDER BY ak.name
                """;

            var result = new List<ApiKeyGroupInfo>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var keyId = reader.GetString(0);
                var keyName = reader.GetString(1);
                var keyPrefix = reader.GetString(2);
                var groupIds = reader.IsDBNull(3)
                    ? []
                    : reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries);
                var groupNames = reader.IsDBNull(4)
                    ? []
                    : reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries);

                result.Add(new ApiKeyGroupInfo(keyId, keyName, keyPrefix, groupIds, groupNames));
            }

            return result;
        }
    }

    public GroupAccess ResolveGroupAccess(string? apiKeyId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(apiKeyId))
        {
            return GroupAccess.Unrestricted;
        }

        lock (_lock)
        {
            var groupIds = GetApiKeyGroupIdsLocked(apiKeyId);
            if (groupIds.Count == 0)
            {
                return GroupAccess.Empty;
            }

            var clientModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT gm.client_id, gm.model, gm.client_pattern
                FROM group_members gm
                INNER JOIN api_key_groups akg ON gm.group_id = akg.group_id
                WHERE akg.api_key_id = $api_key_id
                """;
            command.Parameters.AddWithValue("$api_key_id", apiKeyId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var clientId = reader.IsDBNull(0) ? null : reader.GetString(0);
                var model = reader.IsDBNull(1) ? null : reader.GetString(1);
                var pattern = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    Regex? regex = null;
                    try
                    {
                        regex = new Regex(
                            pattern,
                            RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    }
                    catch (RegexParseException)
                    {
                        continue;
                    }

                    foreach (var connectedClient in _getConnectedClientIds())
                    {
                        if (regex.IsMatch(connectedClient))
                        {
                            if (!string.IsNullOrWhiteSpace(model))
                            {
                                clientModels.Add($"{connectedClient}:{model}");
                            }
                            else
                            {
                                allClients.Add(connectedClient);
                            }
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(clientId))
                {
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        clientModels.Add($"{clientId}:{model}");
                    }
                    else
                    {
                        allClients.Add(clientId);
                    }
                }
            }

            return new GroupAccess(clientModels, allClients);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveClientGroups(IReadOnlyList<string> clientIds)
    {
        if (!_isAvailable || clientIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var clientId in clientIds)
        {
            result[clientId] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT g.name, gm.client_id, gm.client_pattern
                FROM group_members gm
                INNER JOIN groups g ON gm.group_id = g.id
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var groupName = reader.GetString(0);
                var explicitClientId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var pattern = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (!string.IsNullOrWhiteSpace(explicitClientId)
                    && result.TryGetValue(explicitClientId, out var explicitGroups))
                {
                    explicitGroups.Add(groupName);
                }
                else if (!string.IsNullOrWhiteSpace(pattern))
                {
                    Regex? regex = null;
                    try
                    {
                        regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    }
                    catch (RegexParseException)
                    {
                        continue;
                    }

                    foreach (var clientId in clientIds)
                    {
                        if (regex.IsMatch(clientId) && result.TryGetValue(clientId, out var groups))
                        {
                            groups.Add(groupName);
                        }
                    }
                }
            }
        }

        var frozen = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clientId, groups) in result)
        {
            frozen[clientId] = groups.ToList();
        }

        return frozen;
    }

    public GroupBillingInfo? GetGroupBilling(string groupId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT group_id, currency, default_rate_per_1k, refuse_below_balance, enabled, created_at_utc, updated_at_utc
                FROM group_billing WHERE group_id = $group_id
                """;
            command.Parameters.AddWithValue("$group_id", groupId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new GroupBillingInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt32(4) != 0,
                ReadDateTimeOffset(reader.GetString(5)),
                ReadDateTimeOffset(reader.GetString(6)));
        }
    }

    public GroupBillingInfo UpsertGroupBilling(
        string groupId,
        string currency,
        double defaultRatePer1k,
        double refuseBelowBalance,
        bool enabled)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ArgumentException("Group id is required.", nameof(groupId));
        }

        var now = DateTimeOffset.UtcNow;
        var cur = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO group_billing (group_id, currency, default_rate_per_1k, refuse_below_balance, enabled, created_at_utc, updated_at_utc)
                VALUES ($group_id, $currency, $default_rate_per_1k, $refuse_below_balance, $enabled, $created_at_utc, $updated_at_utc)
                ON CONFLICT(group_id) DO UPDATE SET
                    currency = excluded.currency,
                    default_rate_per_1k = excluded.default_rate_per_1k,
                    refuse_below_balance = excluded.refuse_below_balance,
                    enabled = excluded.enabled,
                    updated_at_utc = excluded.updated_at_utc
                """;
            command.Parameters.AddWithValue("$group_id", groupId);
            command.Parameters.AddWithValue("$currency", cur);
            command.Parameters.AddWithValue("$default_rate_per_1k", defaultRatePer1k);
            command.Parameters.AddWithValue("$refuse_below_balance", refuseBelowBalance);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            command.Parameters.AddWithValue("$updated_at_utc", now.ToString("O"));
            command.ExecuteNonQuery();
        }

        return new GroupBillingInfo(groupId, cur, defaultRatePer1k, refuseBelowBalance, enabled, now, now);
    }

    public IReadOnlyList<GroupBillingRule> ListGroupBillingRules(string groupId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId))
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, group_id, model_regex, rate_per_1k, created_at_utc
                FROM group_billing_rules WHERE group_id = $group_id ORDER BY id
                """;
            command.Parameters.AddWithValue("$group_id", groupId);

            var result = new List<GroupBillingRule>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new GroupBillingRule(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    ReadDateTimeOffset(reader.GetString(4))));
            }

            return result;
        }
    }

    public GroupBillingRule AddBillingRule(string groupId, string modelRegex, double ratePer1k)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(modelRegex))
        {
            throw new ArgumentException("Group id and model regex are required.");
        }

        try
        {
            _ = Regex.IsMatch("", modelRegex);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException($"Invalid regex: {ex.Message}", nameof(modelRegex));
        }

        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO group_billing_rules (group_id, model_regex, rate_per_1k, created_at_utc)
                VALUES ($group_id, $model_regex, $rate_per_1k, $created_at_utc)
                """;
            command.Parameters.AddWithValue("$group_id", groupId);
            command.Parameters.AddWithValue("$model_regex", modelRegex.Trim());
            command.Parameters.AddWithValue("$rate_per_1k", ratePer1k);
            command.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            var insertedId = (long)idCommand.ExecuteScalar()!;
            return new GroupBillingRule(insertedId, groupId, modelRegex.Trim(), ratePer1k, now);
        }
    }

    public bool UpdateBillingRule(long ruleId, string modelRegex, double ratePer1k)
    {
        if (!_isAvailable)
        {
            return false;
        }

        try
        {
            _ = Regex.IsMatch("", modelRegex);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException($"Invalid regex: {ex.Message}", nameof(modelRegex));
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE group_billing_rules SET model_regex = $model_regex, rate_per_1k = $rate_per_1k WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", ruleId);
            command.Parameters.AddWithValue("$model_regex", modelRegex.Trim());
            command.Parameters.AddWithValue("$rate_per_1k", ratePer1k);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteBillingRule(long ruleId)
    {
        if (!_isAvailable)
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM group_billing_rules WHERE id = $id";
            command.Parameters.AddWithValue("$id", ruleId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<GroupPayment> ListGroupPayments(string groupId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId))
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, group_id, amount, description, created_at_utc, created_by
                FROM group_payments WHERE group_id = $group_id ORDER BY created_at_utc DESC
                """;
            command.Parameters.AddWithValue("$group_id", groupId);

            var result = new List<GroupPayment>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new GroupPayment(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReadDateTimeOffset(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return result;
        }
    }

    public GroupPayment AddPayment(string groupId, double amount, string? description, string? createdBy)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ArgumentException("Group id is required.", nameof(groupId));
        }

        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO group_payments (group_id, amount, description, created_at_utc, created_by)
                VALUES ($group_id, $amount, $description, $created_at_utc, $created_by)
                """;
            command.Parameters.AddWithValue("$group_id", groupId);
            command.Parameters.AddWithValue("$amount", amount);
            command.Parameters.AddWithValue("$description", string.IsNullOrWhiteSpace(description) ? DBNull.Value : description.Trim());
            command.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            command.Parameters.AddWithValue("$created_by", string.IsNullOrWhiteSpace(createdBy) ? DBNull.Value : createdBy);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            var insertedId = (long)idCommand.ExecuteScalar()!;
            return new GroupPayment(insertedId, groupId, amount, description, now, createdBy);
        }
    }

    public bool DeletePayment(long paymentId)
    {
        if (!_isAvailable)
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM group_payments WHERE id = $id";
            command.Parameters.AddWithValue("$id", paymentId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public GroupBalance GetGroupBalance(string groupId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(groupId))
        {
            return new GroupBalance(groupId ?? "", "EUR", 0, 0, 0);
        }

        lock (_lock)
        {
            using var connection = OpenConnection();

            string currency = "EUR";
            using (var billingCmd = connection.CreateCommand())
            {
                billingCmd.CommandText = "SELECT currency FROM group_billing WHERE group_id = $group_id";
                billingCmd.Parameters.AddWithValue("$group_id", groupId);
                var curResult = billingCmd.ExecuteScalar();
                if (curResult is not null)
                {
                    currency = curResult.ToString() ?? "EUR";
                }
            }

            double totalPayments = 0;
            using (var payCmd = connection.CreateCommand())
            {
                payCmd.CommandText = "SELECT COALESCE(SUM(amount), 0) FROM group_payments WHERE group_id = $group_id";
                payCmd.Parameters.AddWithValue("$group_id", groupId);
                totalPayments = Convert.ToDouble(payCmd.ExecuteScalar());
            }

            double totalCosts = 0;
            using (var costCmd = connection.CreateCommand())
            {
                costCmd.CommandText = """
                    SELECT COALESCE(SUM(rm.cost), 0)
                    FROM request_metrics rm
                    INNER JOIN api_key_groups akg ON rm.api_key_id = akg.api_key_id
                    WHERE akg.group_id = $group_id AND rm.api_key_id IS NOT NULL
                    """;
                costCmd.Parameters.AddWithValue("$group_id", groupId);
                totalCosts = Convert.ToDouble(costCmd.ExecuteScalar());
            }

            return new GroupBalance(groupId, currency, totalPayments, totalCosts, totalPayments - totalCosts);
        }
    }

    public GroupBillingInfo? ResolveBillingForApiKey(string? apiKeyId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(apiKeyId))
        {
            return null;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT gb.group_id, gb.currency, gb.default_rate_per_1k, gb.refuse_below_balance, gb.enabled, gb.created_at_utc, gb.updated_at_utc
                FROM group_billing gb
                INNER JOIN api_key_groups akg ON gb.group_id = akg.group_id
                WHERE akg.api_key_id = $api_key_id AND gb.enabled = 1
                ORDER BY gb.created_at_utc ASC
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$api_key_id", apiKeyId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new GroupBillingInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt32(4) != 0,
                ReadDateTimeOffset(reader.GetString(5)),
                ReadDateTimeOffset(reader.GetString(6)));
        }
    }

    public double CalculateCost(string groupId, string? model, int totalTokens)
    {
        if (totalTokens <= 0)
        {
            return 0;
        }

        lock (_lock)
        {
            var rules = ListGroupBillingRulesLocked(groupId);
            var billing = GetGroupBillingLocked(groupId);

            if (billing is null)
            {
                return 0;
            }

            var ratePer1k = billing.DefaultRatePer1k;

            if (!string.IsNullOrWhiteSpace(model))
            {
                foreach (var rule in rules)
                {
                    try
                    {
                        if (Regex.IsMatch(model, rule.ModelRegex, RegexOptions.IgnoreCase))
                        {
                            ratePer1k = rule.RatePer1k;
                            break;
                        }
                    }
                    catch (RegexParseException)
                    {
                        continue;
                    }
                }
            }

            return (totalTokens / 1000.0) * ratePer1k;
        }
    }

    public (bool Allowed, double Balance, string Currency, double Threshold) CheckBalanceForApiKey(string? apiKeyId)
    {
        var billing = ResolveBillingForApiKey(apiKeyId);
        if (billing is null)
        {
            return (true, 0, "", 0);
        }

        var balance = GetGroupBalance(billing.GroupId);
        var allowed = balance.Balance >= billing.RefuseBelowBalance;
        return (allowed, balance.Balance, billing.Currency, billing.RefuseBelowBalance);
    }

    public IReadOnlyList<TokenStatsByModel> GetTokenStatsByModel()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT model,
                    COALESCE(SUM(prompt_tokens), 0),
                    COALESCE(SUM(completion_tokens), 0),
                    COALESCE(SUM(token_count), 0),
                    COUNT(*)
                FROM request_metrics
                WHERE model IS NOT NULL AND model <> ''
                GROUP BY model
                ORDER BY model
                """;

            var result = new List<TokenStatsByModel>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TokenStatsByModel(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }

            return result;
        }
    }

    public IReadOnlyList<TokenStatsByClient> GetTokenStatsByClient()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT client_id,
                    COALESCE(SUM(prompt_tokens), 0),
                    COALESCE(SUM(completion_tokens), 0),
                    COALESCE(SUM(token_count), 0),
                    COUNT(*)
                FROM request_metrics
                GROUP BY client_id
                ORDER BY client_id
                """;

            var result = new List<TokenStatsByClient>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TokenStatsByClient(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }

            return result;
        }
    }

    public IReadOnlyList<TokenStatsByApiKey> GetTokenStatsByApiKey()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT rm.api_key_id, COALESCE(ak.name, 'Unknown'), COALESCE(ak.key_prefix, ''),
                    COALESCE(SUM(rm.prompt_tokens), 0),
                    COALESCE(SUM(rm.completion_tokens), 0),
                    COALESCE(SUM(rm.token_count), 0),
                    COUNT(*)
                FROM request_metrics rm
                LEFT JOIN api_keys ak ON rm.api_key_id = ak.id
                WHERE rm.api_key_id IS NOT NULL
                GROUP BY rm.api_key_id
                ORDER BY ak.name
                """;

            var result = new List<TokenStatsByApiKey>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TokenStatsByApiKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6)));
            }

            return result;
        }
    }

    public IReadOnlyList<TokenStatsByGroup> GetTokenStatsByGroup()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT akg.group_id, COALESCE(g.name, 'Unknown'),
                    COALESCE(SUM(rm.prompt_tokens), 0),
                    COALESCE(SUM(rm.completion_tokens), 0),
                    COALESCE(SUM(rm.token_count), 0),
                    COUNT(*)
                FROM request_metrics rm
                INNER JOIN api_key_groups akg ON rm.api_key_id = akg.api_key_id
                INNER JOIN groups g ON akg.group_id = g.id
                WHERE rm.api_key_id IS NOT NULL
                GROUP BY akg.group_id
                ORDER BY g.name
                """;

            var result = new List<TokenStatsByGroup>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TokenStatsByGroup(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5)));
            }

            return result;
        }
    }

    public IReadOnlyList<ClientRevenue> GetClientRevenue()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT rm.client_id,
                    COALESCE(SUM(rm.cost), 0)
                FROM request_metrics rm
                WHERE rm.api_key_id IS NOT NULL AND rm.cost > 0
                GROUP BY rm.client_id
                ORDER BY rm.client_id
                """;

            var result = new List<ClientRevenue>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var clientId = reader.GetString(0);
                var revenue = reader.GetDouble(1);

                var billing = ResolveBillingForApiKeyForClientLocked(clientId);
                var currency = billing?.Currency ?? "EUR";

                result.Add(new ClientRevenue(clientId, revenue, currency));
            }

            return result;
        }
    }

    public IReadOnlyList<GroupBillingRule> ListGroupBillingRulesLocked(string groupId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, group_id, model_regex, rate_per_1k, created_at_utc
            FROM group_billing_rules WHERE group_id = $group_id ORDER BY id
            """;
        command.Parameters.AddWithValue("$group_id", groupId);

        var result = new List<GroupBillingRule>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new GroupBillingRule(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                ReadDateTimeOffset(reader.GetString(4))));
        }

        return result;
    }

    private GroupBillingInfo? GetGroupBillingLocked(string groupId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_id, currency, default_rate_per_1k, refuse_below_balance, enabled, created_at_utc, updated_at_utc
            FROM group_billing WHERE group_id = $group_id
            """;
        command.Parameters.AddWithValue("$group_id", groupId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GroupBillingInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.GetInt32(4) != 0,
            ReadDateTimeOffset(reader.GetString(5)),
            ReadDateTimeOffset(reader.GetString(6)));
    }

    private GroupBillingInfo? ResolveBillingForApiKeyForClientLocked(string clientId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT gb.group_id, gb.currency, gb.default_rate_per_1k, gb.refuse_below_balance, gb.enabled, gb.created_at_utc, gb.updated_at_utc
            FROM group_billing gb
            INNER JOIN api_key_groups akg ON gb.group_id = akg.group_id
            INNER JOIN request_metrics rm ON rm.api_key_id = akg.api_key_id
            WHERE rm.client_id = $client_id AND gb.enabled = 1
            ORDER BY gb.created_at_utc ASC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$client_id", clientId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GroupBillingInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.GetInt32(4) != 0,
            ReadDateTimeOffset(reader.GetString(5)),
            ReadDateTimeOffset(reader.GetString(6)));
    }

    private IReadOnlyList<string> GetApiKeyGroupIdsLocked(string apiKeyId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_id FROM api_key_groups WHERE api_key_id = $api_key_id";
        command.Parameters.AddWithValue("$api_key_id", apiKeyId);

        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private Func<IEnumerable<string>> _getConnectedClientIds = () => [];

    public void SetConnectedClientProvider(Func<IEnumerable<string>> provider)
    {
        _getConnectedClientIds = provider;
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

                CREATE TABLE IF NOT EXISTS groups (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS group_members (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                    client_id TEXT NULL,
                    model TEXT NULL,
                    client_pattern TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_group_members_group_id
                    ON group_members (group_id);

                CREATE TABLE IF NOT EXISTS api_key_groups (
                    api_key_id TEXT NOT NULL REFERENCES api_keys(id) ON DELETE CASCADE,
                    group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                    PRIMARY KEY (api_key_id, group_id)
                );
                """;
            command.ExecuteNonQuery();

        using (var migrate = connection.CreateCommand())
        {
            migrate.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('request_metrics') WHERE name = 'prompt_tokens'
                """;
            var hasColumn = (long)migrate.ExecuteScalar()! > 0;

            if (!hasColumn)
            {
                using var alter1 = connection.CreateCommand();
                alter1.CommandText = "ALTER TABLE request_metrics ADD COLUMN prompt_tokens INTEGER NOT NULL DEFAULT 0";
                alter1.ExecuteNonQuery();

                using var alter2 = connection.CreateCommand();
                alter2.CommandText = "ALTER TABLE request_metrics ADD COLUMN completion_tokens INTEGER NOT NULL DEFAULT 0";
                alter2.ExecuteNonQuery();

                using var alter3 = connection.CreateCommand();
                alter3.CommandText = "ALTER TABLE request_metrics ADD COLUMN api_key_id TEXT NULL";
                alter3.ExecuteNonQuery();

                using var alter4 = connection.CreateCommand();
                alter4.CommandText = "ALTER TABLE request_metrics ADD COLUMN cost REAL NOT NULL DEFAULT 0";
                alter4.ExecuteNonQuery();

                using var idx = connection.CreateCommand();
                idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_request_metrics_api_key ON request_metrics (api_key_id, started_at_utc)";
                idx.ExecuteNonQuery();
            }
        }

        using (var command2 = connection.CreateCommand())
        {
            command2.CommandText = """
                CREATE TABLE IF NOT EXISTS group_billing (
                    group_id              TEXT NOT NULL PRIMARY KEY REFERENCES groups(id) ON DELETE CASCADE,
                    currency              TEXT NOT NULL DEFAULT 'EUR',
                    default_rate_per_1k   REAL NOT NULL DEFAULT 0.0,
                    refuse_below_balance  REAL NOT NULL DEFAULT 0.0,
                    enabled               INTEGER NOT NULL DEFAULT 0,
                    created_at_utc        TEXT NOT NULL,
                    updated_at_utc        TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS group_billing_rules (
                    id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    group_id        TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                    model_regex     TEXT NOT NULL,
                    rate_per_1k     REAL NOT NULL,
                    created_at_utc  TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_group_billing_rules_group ON group_billing_rules (group_id);

                CREATE TABLE IF NOT EXISTS group_payments (
                    id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    group_id        TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                    amount          REAL NOT NULL,
                    description     TEXT NULL,
                    created_at_utc  TEXT NOT NULL,
                    created_by      TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_group_payments_group ON group_payments (group_id);
                """;
            command2.ExecuteNonQuery();
        }
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
    int PromptTokens,
    int CompletionTokens,
    int TokenCount,
    string? ApiKeyId,
    double Cost,
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

internal sealed record GroupBillingInfo(
    string GroupId,
    string Currency,
    double DefaultRatePer1k,
    double RefuseBelowBalance,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record GroupBillingRule(
    long Id,
    string GroupId,
    string ModelRegex,
    double RatePer1k,
    DateTimeOffset CreatedAtUtc);

internal sealed record GroupPayment(
    long Id,
    string GroupId,
    double Amount,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy);

internal sealed record GroupBalance(
    string GroupId,
    string Currency,
    double TotalPayments,
    double TotalCosts,
    double Balance);

internal sealed record TokenStatsByModel(
    string Model,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long Requests);

internal sealed record TokenStatsByClient(
    string ClientId,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long Requests);

internal sealed record TokenStatsByApiKey(
    string ApiKeyId,
    string ApiKeyName,
    string ApiKeyPrefix,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long Requests);

internal sealed record TokenStatsByGroup(
    string GroupId,
    string GroupName,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long Requests);

internal sealed record ClientRevenue(
    string ClientId,
    double Revenue,
    string Currency);

internal sealed record GroupInfo(
    string Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record GroupClientInfo(
    long Id,
    string GroupId,
    string? ClientId,
    string? Model,
    string? ClientPattern);

internal sealed record ApiKeyGroupInfo(
    string ApiKeyId,
    string ApiKeyName,
    string ApiKeyPrefix,
    IReadOnlyList<string> GroupIds,
    IReadOnlyList<string> GroupNames);

internal sealed class GroupAccess
{
    public static GroupAccess Unrestricted { get; } = new(new HashSet<string>(), new HashSet<string>(), isUnrestricted: true);

    public static GroupAccess Empty { get; } = new(new HashSet<string>(), new HashSet<string>());

    public IReadOnlySet<string> ClientModels { get; }

    public IReadOnlySet<string> AllClients { get; }

    public bool IsUnrestricted { get; }

    public GroupAccess(IReadOnlySet<string> clientModels, IReadOnlySet<string> allClients, bool isUnrestricted = false)
    {
        ClientModels = clientModels;
        AllClients = allClients;
        IsUnrestricted = isUnrestricted;
    }

    public bool IsClientAllowed(string clientId) =>
        IsUnrestricted || AllClients.Contains(clientId);

    public bool IsClientModelAllowed(string clientId, string model) =>
        IsUnrestricted
        || AllClients.Contains(clientId)
        || ClientModels.Contains($"{clientId}:{model}");
}
