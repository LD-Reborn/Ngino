using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Ngino.Server;

internal sealed class ManagementStore
{
    private static readonly TimeSpan UserKeyLastUsedWriteInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, KeyState> _userKeysByHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeyState> _clientKeysByHash = new(StringComparer.Ordinal);
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

    public bool HasUserKeys
    {
        get
        {
            if (!_isAvailable)
            {
                return false;
            }

            lock (_lock)
            {
                return _userKeysByHash.Count > 0;
            }
        }
    }

    public bool HasClientKeys
    {
        get
        {
            if (!_isAvailable)
            {
                return false;
            }

            lock (_lock)
            {
                return _clientKeysByHash.Count > 0;
            }
        }
    }

    public bool IsUserKeyValid(string userKey, bool updateLastUsed)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(userKey))
        {
            return false;
        }

        var hash = HashKey(userKey);
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (!_userKeysByHash.TryGetValue(hash, out var key))
            {
                return false;
            }

            if (!updateLastUsed
                || key.LastUsedUtc is not null
                && now - key.LastUsedUtc.Value < UserKeyLastUsedWriteInterval)
            {
                return true;
            }

            key.LastUsedUtc = now;

            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE user_keys SET last_used_at_utc = $last_used_at_utc WHERE id = $id";
                command.Parameters.AddWithValue("$last_used_at_utc", now.ToString("O"));
                command.Parameters.AddWithValue("$id", key.Id);
                command.ExecuteNonQuery();
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Failed to update user key last-used timestamp.");
            }

            return true;
        }
    }

    public IReadOnlyList<UserKeyInfo> ListUserKeys()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            return _userKeysByHash.Values
                .OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(key => key.CreatedAtUtc)
                .Select(key => new UserKeyInfo(
                    key.Id,
                    key.Name,
                    key.KeyPrefix,
                    key.CreatedAtUtc,
                    key.LastUsedUtc))
                .ToList();
        }
    }

    public CreatedUserKey CreateUserKey(string? name)
    {
        EnsureAvailable();

        var key = GenerateKey();
        var now = DateTimeOffset.UtcNow;
        var state = new KeyState
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = string.IsNullOrWhiteSpace(name) ? "User key" : name.Trim(),
            KeyHash = HashKey(key),
            KeyPrefix = GetKeyPrefix(key),
            CreatedAtUtc = now
        };

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO user_keys (id, name, key_hash, key_prefix, created_at_utc)
                VALUES ($id, $name, $key_hash, $key_prefix, $created_at_utc)
                """;
            command.Parameters.AddWithValue("$id", state.Id);
            command.Parameters.AddWithValue("$name", state.Name);
            command.Parameters.AddWithValue("$key_hash", state.KeyHash);
            command.Parameters.AddWithValue("$key_prefix", state.KeyPrefix);
            command.Parameters.AddWithValue("$created_at_utc", state.CreatedAtUtc.ToString("O"));
            command.ExecuteNonQuery();

            _userKeysByHash[state.KeyHash] = state;
        }

        return new CreatedUserKey(
            state.Id,
            state.Name,
            state.KeyPrefix,
            state.CreatedAtUtc,
            key);
    }

    public bool DeleteUserKey(string id)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM user_keys WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            var deleted = command.ExecuteNonQuery() > 0;

            if (deleted)
            {
                foreach (var pair in _userKeysByHash.Where(pair => pair.Value.Id == id).ToArray())
                {
                    _userKeysByHash.Remove(pair.Key);
                }
            }

            return deleted;
        }
    }

    public bool IsClientKeyValid(string clientKey, bool updateLastUsed)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(clientKey))
        {
            return false;
        }

        var hash = HashKey(clientKey);
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (!_clientKeysByHash.TryGetValue(hash, out var key))
            {
                return false;
            }

            if (!updateLastUsed
                || key.LastUsedUtc is not null
                && now - key.LastUsedUtc.Value < UserKeyLastUsedWriteInterval)
            {
                return true;
            }

            key.LastUsedUtc = now;

            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE client_keys SET last_used_at_utc = $last_used_at_utc WHERE id = $id";
                command.Parameters.AddWithValue("$last_used_at_utc", now.ToString("O"));
                command.Parameters.AddWithValue("$id", key.Id);
                command.ExecuteNonQuery();
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Failed to update client key last-used timestamp.");
            }

            return true;
        }
    }

    public IReadOnlyList<UserKeyInfo> ListClientKeys()
    {
        if (!_isAvailable)
        {
            return [];
        }

        lock (_lock)
        {
            return _clientKeysByHash.Values
                .OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(key => key.CreatedAtUtc)
                .Select(key => new UserKeyInfo(
                    key.Id,
                    key.Name,
                    key.KeyPrefix,
                    key.CreatedAtUtc,
                    key.LastUsedUtc))
                .ToList();
        }
    }

    public CreatedUserKey CreateClientKey(string? name)
    {
        EnsureAvailable();

        var key = GenerateKey();
        var now = DateTimeOffset.UtcNow;
        var state = new KeyState
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = string.IsNullOrWhiteSpace(name) ? "Client key" : name.Trim(),
            KeyHash = HashKey(key),
            KeyPrefix = GetKeyPrefix(key),
            CreatedAtUtc = now
        };

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO client_keys (id, name, key_hash, key_prefix, created_at_utc)
                VALUES ($id, $name, $key_hash, $key_prefix, $created_at_utc)
                """;
            command.Parameters.AddWithValue("$id", state.Id);
            command.Parameters.AddWithValue("$name", state.Name);
            command.Parameters.AddWithValue("$key_hash", state.KeyHash);
            command.Parameters.AddWithValue("$key_prefix", state.KeyPrefix);
            command.Parameters.AddWithValue("$created_at_utc", state.CreatedAtUtc.ToString("O"));
            command.ExecuteNonQuery();

            _clientKeysByHash[state.KeyHash] = state;
        }

        return new CreatedUserKey(
            state.Id,
            state.Name,
            state.KeyPrefix,
            state.CreatedAtUtc,
            key);
    }

    public bool DeleteClientKey(string id)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM client_keys WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            var deleted = command.ExecuteNonQuery() > 0;

            if (deleted)
            {
                foreach (var pair in _clientKeysByHash.Where(pair => pair.Value.Id == id).ToArray())
                {
                    _clientKeysByHash.Remove(pair.Key);
                }
            }

            return deleted;
        }
    }

    public string? GetClientKeyId(string clientKey)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(clientKey))
        {
            return null;
        }

        var hash = HashKey(clientKey);

        lock (_lock)
        {
            return _clientKeysByHash.TryGetValue(hash, out var key) ? key.Id : null;
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
                SELECT disabled_until_utc, disabled_manually, disabled_reason, disabled_from_utc
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
            var disabledFrom = ReadNullableDateTimeOffset(reader, 3);

            var now = DateTimeOffset.UtcNow;
            var isScheduled = disabledFrom > now;

            if (disabledManually && !isScheduled)
            {
                return new ClientAccess(true, disabledFrom, null, true, reason);
            }

            if (!isScheduled && disabledUntil is { } until && until > now)
            {
                return new ClientAccess(true, disabledFrom, until, false, reason);
            }

            return new ClientAccess(false, disabledFrom, disabledUntil, false, reason);
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
            command.CommandText = "SELECT client_id, disabled_until_utc, disabled_manually, disabled_reason, disabled_from_utc FROM client_controls";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var clientId = reader.GetString(0);
                var disabledUntil = ReadNullableDateTimeOffset(reader, 1);
                var disabledManually = reader.GetInt32(2) != 0;
                var reason = reader.IsDBNull(3) ? null : reader.GetString(3);
                var disabledFrom = ReadNullableDateTimeOffset(reader, 4);

                var now = DateTimeOffset.UtcNow;
                var isScheduled = disabledFrom > now;

                result[clientId] = disabledManually && !isScheduled
                    ? new ClientAccess(true, disabledFrom, null, true, reason)
                    : !isScheduled && disabledUntil is { } until && until > now
                        ? new ClientAccess(true, disabledFrom, until, false, reason)
                        : new ClientAccess(false, disabledFrom, disabledUntil, disabledManually, reason);
            }
        }

        return result;
    }

    public void DisableClient(string clientId, TimeSpan? duration, bool manually, string? reason, DateTimeOffset? startAtUtc = null, DateTimeOffset? untilUtc = null)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id is required.", nameof(clientId));
        }

        var now = DateTimeOffset.UtcNow;
        var disabledFrom = startAtUtc?.ToString("O");
        var disabledUntil = untilUtc?.ToString("O")
            ?? (manually ? null : now.Add(duration ?? TimeSpan.FromHours(1)).ToString("O"));

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO client_controls (
                    client_id,
                    disabled_until_utc,
                    disabled_from_utc,
                    disabled_manually,
                    disabled_reason,
                    updated_at_utc)
                VALUES (
                    $client_id,
                    $disabled_until_utc,
                    $disabled_from_utc,
                    $disabled_manually,
                    $disabled_reason,
                    $updated_at_utc)
                ON CONFLICT(client_id) DO UPDATE SET
                    disabled_until_utc = excluded.disabled_until_utc,
                    disabled_from_utc = excluded.disabled_from_utc,
                    disabled_manually = excluded.disabled_manually,
                    disabled_reason = excluded.disabled_reason,
                    updated_at_utc = excluded.updated_at_utc
                """;
            command.Parameters.AddWithValue("$client_id", clientId);
            command.Parameters.AddWithValue("$disabled_until_utc", (object?)disabledUntil ?? DBNull.Value);
            command.Parameters.AddWithValue("$disabled_from_utc", (object?)disabledFrom ?? DBNull.Value);
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
                    disabled_from_utc,
                    disabled_manually,
                    disabled_reason,
                    updated_at_utc)
                VALUES (
                    $client_id,
                    NULL,
                    NULL,
                    0,
                    NULL,
                    $updated_at_utc)
                ON CONFLICT(client_id) DO UPDATE SET
                    disabled_until_utc = NULL,
                    disabled_from_utc = NULL,
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
                        user_key_id,
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
                        $user_key_id,
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
                command.Parameters.AddWithValue("$user_key_id", string.IsNullOrWhiteSpace(metric.UserKeyId) ? DBNull.Value : metric.UserKeyId);
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

    public string? GetUserKeyId(string userKey)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(userKey))
        {
            return null;
        }

        var hash = HashKey(userKey);

        lock (_lock)
        {
            return _userKeysByHash.TryGetValue(hash, out var key) ? key.Id : null;
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
                SELECT id, group_id, client_id, model, client_pattern,
                       keepalive_instances_to_keep_alive,
                       keepalive_max_parallelism_per_client,
                       keepalive_parallelism_headroom
                FROM group_members
                WHERE group_id = $group_id
                ORDER BY client_id, model, client_pattern
                """;
            command.Parameters.AddWithValue("$group_id", groupId);

            return ReadGroupClientInfos(command);
        }
    }

    public IReadOnlyList<GroupClientInfo> ListAllGroupClients()
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
                SELECT id, group_id, client_id, model, client_pattern,
                       keepalive_instances_to_keep_alive,
                       keepalive_max_parallelism_per_client,
                       keepalive_parallelism_headroom
                FROM group_members
                ORDER BY client_id, model, client_pattern
                """;

            return ReadGroupClientInfos(command);
        }
    }

    private List<GroupClientInfo> ReadGroupClientInfos(SqliteCommand command)
    {
        var result = new List<GroupClientInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new GroupClientInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ReadKeepalivePolicy(reader, 5, 6, 7)));
        }

        return result;
    }

    public GroupClientInfo AddGroupClient(
        string groupId,
        string? clientId,
        string? model,
        string? clientPattern,
        int? keepaliveInstancesToKeepAlive,
        int? keepaliveMaxParallelismPerClient,
        int? keepaliveParallelismHeadroom)
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

        var policy = NormalizeKeepalivePolicy(
            keepaliveInstancesToKeepAlive,
            keepaliveMaxParallelismPerClient,
            keepaliveParallelismHeadroom);

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO group_members (
                    group_id,
                    client_id,
                    model,
                    client_pattern,
                    keepalive_instances_to_keep_alive,
                    keepalive_max_parallelism_per_client,
                    keepalive_parallelism_headroom)
                VALUES (
                    $group_id,
                    $client_id,
                    $model,
                    $client_pattern,
                    $keepalive_instances_to_keep_alive,
                    $keepalive_max_parallelism_per_client,
                    $keepalive_parallelism_headroom)
                """;
            command.Parameters.AddWithValue("$group_id", groupId);
            command.Parameters.AddWithValue("$client_id", string.IsNullOrWhiteSpace(clientId) ? DBNull.Value : clientId);
            command.Parameters.AddWithValue("$model", string.IsNullOrWhiteSpace(model) ? DBNull.Value : model);
            command.Parameters.AddWithValue("$client_pattern", string.IsNullOrWhiteSpace(clientPattern) ? DBNull.Value : clientPattern);
            command.Parameters.AddWithValue("$keepalive_instances_to_keep_alive", policy.InstancesToKeepAlive);
            command.Parameters.AddWithValue("$keepalive_max_parallelism_per_client", policy.MaxParallelismPerClient);
            command.Parameters.AddWithValue("$keepalive_parallelism_headroom", policy.ParallelismHeadroom);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            var insertedId = (long)idCommand.ExecuteScalar()!;
            return new GroupClientInfo(insertedId, groupId, clientId, model, clientPattern, policy);
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

    public IReadOnlyList<string> GetUserKeyGroupIds(string userKeyId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(userKeyId))
        {
            return [];
        }

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT g.id FROM groups g
                INNER JOIN user_key_groups ukg ON g.id = ukg.group_id
                WHERE ukg.user_key_id = $user_key_id
                ORDER BY g.name
                """;
            command.Parameters.AddWithValue("$user_key_id", userKeyId);

            var result = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
    }

    public void SetUserKeyGroups(string userKeyId, IReadOnlyList<string> groupIds)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(userKeyId))
        {
            throw new ArgumentException("User key id is required.", nameof(userKeyId));
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
                    deleteCommand.CommandText = "DELETE FROM user_key_groups WHERE user_key_id = $user_key_id";
                    deleteCommand.Parameters.AddWithValue("$user_key_id", userKeyId);
                    deleteCommand.ExecuteNonQuery();
                }

                using (var insertCommand = connection.CreateCommand())
                {
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = """
                        INSERT INTO user_key_groups (user_key_id, group_id)
                        VALUES ($user_key_id, $group_id)
                        """;

                    var userKeyParam = insertCommand.Parameters.Add("$user_key_id", SqliteType.Text);
                    var groupParam = insertCommand.Parameters.Add("$group_id", SqliteType.Text);
                    userKeyParam.Value = userKeyId;

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

    public IReadOnlyList<UserKeyGroupInfo> ListUserKeyGroups()
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
                SELECT uk.id, uk.name, uk.key_prefix,
                    GROUP_CONCAT(g.id) as group_ids,
                    GROUP_CONCAT(g.name) as group_names
                FROM user_keys uk
                LEFT JOIN user_key_groups ukg ON uk.id = ukg.user_key_id
                LEFT JOIN groups g ON ukg.group_id = g.id
                GROUP BY uk.id
                ORDER BY uk.name
                """;

            var result = new List<UserKeyGroupInfo>();
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

                result.Add(new UserKeyGroupInfo(keyId, keyName, keyPrefix, groupIds, groupNames));
            }

            return result;
        }
    }

    public GroupAccess ResolveGroupAccess(string? userKeyId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(userKeyId))
        {
            return GroupAccess.Unrestricted;
        }

        lock (_lock)
        {
            var groupIds = GetUserKeyGroupIdsLocked(userKeyId);
            if (groupIds.Count == 0)
            {
                return GroupAccess.Unrestricted;
            }

            var clientModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT gm.client_id, gm.model, gm.client_pattern
                FROM group_members gm
                INNER JOIN user_key_groups ukg ON gm.group_id = ukg.group_id
                WHERE ukg.user_key_id = $user_key_id
                """;
            command.Parameters.AddWithValue("$user_key_id", userKeyId);

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
                SELECT g.id, gm.client_id, gm.client_pattern
                FROM group_members gm
                INNER JOIN groups g ON gm.group_id = g.id
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var groupId = reader.GetString(0);
                var explicitClientId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var pattern = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (!string.IsNullOrWhiteSpace(explicitClientId)
                    && result.TryGetValue(explicitClientId, out var explicitGroups))
                {
                    explicitGroups.Add(groupId);
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
                            groups.Add(groupId);
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
                    INNER JOIN user_key_groups ukg ON rm.user_key_id = ukg.user_key_id
                    WHERE ukg.group_id = $group_id AND rm.user_key_id IS NOT NULL
                    """;
                costCmd.Parameters.AddWithValue("$group_id", groupId);
                totalCosts = Convert.ToDouble(costCmd.ExecuteScalar());
            }

            return new GroupBalance(groupId, currency, totalPayments, totalCosts, totalPayments - totalCosts);
        }
    }

    public GroupBillingInfo? ResolveBillingForUserKey(string? userKeyId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(userKeyId))
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
                INNER JOIN user_key_groups ukg ON gb.group_id = ukg.group_id
                WHERE ukg.user_key_id = $user_key_id AND gb.enabled = 1
                ORDER BY gb.created_at_utc ASC
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$user_key_id", userKeyId);

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

    public (bool Allowed, double Balance, string Currency, double Threshold) CheckBalanceForUserKey(string? userKeyId)
    {
        var billing = ResolveBillingForUserKey(userKeyId);
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

    public IReadOnlyList<TokenStatsByUserKey> GetTokenStatsByUserKey()
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
                SELECT rm.user_key_id, COALESCE(uk.name, 'Unknown'), COALESCE(uk.key_prefix, ''),
                    COALESCE(SUM(rm.prompt_tokens), 0),
                    COALESCE(SUM(rm.completion_tokens), 0),
                    COALESCE(SUM(rm.token_count), 0),
                    COUNT(*)
                FROM request_metrics rm
                LEFT JOIN user_keys uk ON rm.user_key_id = uk.id
                WHERE rm.user_key_id IS NOT NULL
                GROUP BY rm.user_key_id
                ORDER BY uk.name
                """;

            var result = new List<TokenStatsByUserKey>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TokenStatsByUserKey(
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
                INNER JOIN user_key_groups akg ON rm.user_key_id = akg.user_key_id
                INNER JOIN groups g ON akg.group_id = g.id
                WHERE rm.user_key_id IS NOT NULL
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
                WHERE rm.user_key_id IS NOT NULL AND rm.cost > 0
                GROUP BY rm.client_id
                ORDER BY rm.client_id
                """;

            var result = new List<ClientRevenue>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var clientId = reader.GetString(0);
                var revenue = reader.GetDouble(1);

                var billing = ResolveBillingForUserKeyForClientLocked(clientId);
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

    private GroupBillingInfo? ResolveBillingForUserKeyForClientLocked(string clientId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT gb.group_id, gb.currency, gb.default_rate_per_1k, gb.refuse_below_balance, gb.enabled, gb.created_at_utc, gb.updated_at_utc
            FROM group_billing gb
            INNER JOIN user_key_groups ukg ON gb.group_id = ukg.group_id
            INNER JOIN request_metrics rm ON rm.user_key_id = ukg.user_key_id
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

    private IReadOnlyList<string> GetUserKeyGroupIdsLocked(string userKeyId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_id FROM user_key_groups WHERE user_key_id = $user_key_id";
        command.Parameters.AddWithValue("$user_key_id", userKeyId);

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
                    disabled_from_utc TEXT NULL,
                    disabled_manually INTEGER NOT NULL DEFAULT 0,
                    disabled_reason TEXT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS user_keys (
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
                    prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    completion_tokens INTEGER NOT NULL DEFAULT 0,
                    user_key_id TEXT NULL,
                    cost REAL NOT NULL DEFAULT 0,
                    started_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT NOT NULL,
                    duration_ms REAL NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_request_metrics_client_started
                    ON request_metrics (client_id, started_at_utc);

                CREATE INDEX IF NOT EXISTS idx_request_metrics_model_started
                    ON request_metrics (model, started_at_utc);

                CREATE INDEX IF NOT EXISTS idx_request_metrics_user_key
                    ON request_metrics (user_key_id, started_at_utc);

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
                    client_pattern TEXT NULL,
                    keepalive_instances_to_keep_alive INTEGER NOT NULL DEFAULT 1,
                    keepalive_max_parallelism_per_client INTEGER NOT NULL DEFAULT 1,
                    keepalive_parallelism_headroom INTEGER NOT NULL DEFAULT 1
                );

                CREATE INDEX IF NOT EXISTS idx_group_members_group_id
                    ON group_members (group_id);

                CREATE TABLE IF NOT EXISTS user_key_groups (
                    user_key_id TEXT NOT NULL REFERENCES user_keys(id) ON DELETE CASCADE,
                    group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                    PRIMARY KEY (user_key_id, group_id)
                );

                CREATE TABLE IF NOT EXISTS client_keys (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    key_hash TEXT NOT NULL UNIQUE,
                    key_prefix TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    last_used_at_utc TEXT NULL
                );
                """;
            command.ExecuteNonQuery();

        using (var migrate = connection.CreateCommand())
        {
            migrate.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('group_members') WHERE name = 'keepalive_instances_to_keep_alive'
                """;
            var hasKeepaliveInstancesColumn = (long)migrate.ExecuteScalar()! > 0;

            if (!hasKeepaliveInstancesColumn)
            {
                using var addKeepaliveInstances = connection.CreateCommand();
                addKeepaliveInstances.CommandText = "ALTER TABLE group_members ADD COLUMN keepalive_instances_to_keep_alive INTEGER NOT NULL DEFAULT 1";
                addKeepaliveInstances.ExecuteNonQuery();

                using var addKeepaliveMaxParallelism = connection.CreateCommand();
                addKeepaliveMaxParallelism.CommandText = "ALTER TABLE group_members ADD COLUMN keepalive_max_parallelism_per_client INTEGER NOT NULL DEFAULT 1";
                addKeepaliveMaxParallelism.ExecuteNonQuery();

                using var addKeepaliveHeadroom = connection.CreateCommand();
                addKeepaliveHeadroom.CommandText = "ALTER TABLE group_members ADD COLUMN keepalive_parallelism_headroom INTEGER NOT NULL DEFAULT 1";
                addKeepaliveHeadroom.ExecuteNonQuery();
            }
        }

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
                alter3.CommandText = "ALTER TABLE request_metrics ADD COLUMN user_key_id TEXT NULL";
                alter3.ExecuteNonQuery();

                using var alter4 = connection.CreateCommand();
                alter4.CommandText = "ALTER TABLE request_metrics ADD COLUMN cost REAL NOT NULL DEFAULT 0";
                alter4.ExecuteNonQuery();

                using var idx = connection.CreateCommand();
                idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_request_metrics_user_key ON request_metrics (user_key_id, started_at_utc)";
                idx.ExecuteNonQuery();
            }
            else
            {
                using var checkCol = connection.CreateCommand();
                checkCol.CommandText = "SELECT COUNT(*) FROM pragma_table_info('request_metrics') WHERE name = 'api_key_id'";
                var hasOldColumn = (long)checkCol.ExecuteScalar()! > 0;

                if (hasOldColumn)
                {
                    using var rename = connection.CreateCommand();
                    rename.CommandText = "ALTER TABLE request_metrics RENAME COLUMN api_key_id TO user_key_id";
                    rename.ExecuteNonQuery();

                    using var idx = connection.CreateCommand();
                    idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_request_metrics_user_key ON request_metrics (user_key_id, started_at_utc)";
                    idx.ExecuteNonQuery();
                }
                else
                {
                    using var checkNew = connection.CreateCommand();
                    checkNew.CommandText = "SELECT COUNT(*) FROM pragma_table_info('request_metrics') WHERE name = 'user_key_id'";
                    var hasNewColumn = (long)checkNew.ExecuteScalar()! > 0;

                    if (!hasNewColumn)
                    {
                        using var addCol = connection.CreateCommand();
                        addCol.CommandText = "ALTER TABLE request_metrics ADD COLUMN user_key_id TEXT NULL";
                        addCol.ExecuteNonQuery();

                        using var idx = connection.CreateCommand();
                        idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_request_metrics_user_key ON request_metrics (user_key_id, started_at_utc)";
                        idx.ExecuteNonQuery();
                    }
                }
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

        using (var migrate = connection.CreateCommand())
        {
            migrate.CommandText = """
                SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='api_key_groups'
                """;
            var hasOldTable = (long)migrate.ExecuteScalar()! > 0;

            if (hasOldTable)
            {
                using var rename = connection.CreateCommand();
                rename.CommandText = "ALTER TABLE api_key_groups RENAME TO user_key_groups";
                rename.ExecuteNonQuery();

                using var renameCol = connection.CreateCommand();
                renameCol.CommandText = "ALTER TABLE user_key_groups RENAME COLUMN api_key_id TO user_key_id";
                renameCol.ExecuteNonQuery();
            }
            else
            {
                migrate.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='user_key_groups'
                    """;
                var hasNewTable = (long)migrate.ExecuteScalar()! > 0;

                if (hasNewTable)
                {
                    using var checkCol = connection.CreateCommand();
                    checkCol.CommandText = "SELECT COUNT(*) FROM pragma_table_info('user_key_groups') WHERE name = 'api_key_id'";
                    var hasOldCol = (long)checkCol.ExecuteScalar()! > 0;

                    if (hasOldCol)
                    {
                        using var renameCol = connection.CreateCommand();
                        renameCol.CommandText = "ALTER TABLE user_key_groups RENAME COLUMN api_key_id TO user_key_id";
                        renameCol.ExecuteNonQuery();
                    }
                }
            }

            migrate.CommandText = """
                SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='api_keys'
                """;
            var hasOldApiKeys = (long)migrate.ExecuteScalar()! > 0;

            if (hasOldApiKeys)
            {
                using var rename = connection.CreateCommand();
                rename.CommandText = "ALTER TABLE api_keys RENAME TO user_keys";
                rename.ExecuteNonQuery();
            }
        }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, key_hash, key_prefix, created_at_utc, last_used_at_utc
                FROM user_keys
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var state = new KeyState
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    KeyHash = reader.GetString(2),
                    KeyPrefix = reader.GetString(3),
                    CreatedAtUtc = ReadDateTimeOffset(reader.GetString(4)),
                    LastUsedUtc = ReadNullableDateTimeOffset(reader, 5)
                };

                _userKeysByHash[state.KeyHash] = state;
            }
        }

        _logger.LogInformation(
            "Loaded {UserKeyCount} user key(s) from {DatabasePath}.",
            _userKeysByHash.Count,
            _databasePath);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, key_hash, key_prefix, created_at_utc, last_used_at_utc
                FROM client_keys
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var state = new KeyState
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    KeyHash = reader.GetString(2),
                    KeyPrefix = reader.GetString(3),
                    CreatedAtUtc = ReadDateTimeOffset(reader.GetString(4)),
                    LastUsedUtc = ReadNullableDateTimeOffset(reader, 5)
                };

                _clientKeysByHash[state.KeyHash] = state;
            }
        }

        using (var migrate = connection.CreateCommand())
        {
            migrate.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('client_controls') WHERE name = 'disabled_from_utc'
                """;
            var hasFromColumn = (long)migrate.ExecuteScalar()! > 0;

            if (!hasFromColumn)
            {
                using var addFromCol = connection.CreateCommand();
                addFromCol.CommandText = "ALTER TABLE client_controls ADD COLUMN disabled_from_utc TEXT NULL";
                addFromCol.ExecuteNonQuery();
            }
        }

        _logger.LogInformation(
            "Loaded {ClientKeyCount} client key(s) from {DatabasePath}.",
            _clientKeysByHash.Count,
            _databasePath);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static GroupClientKeepalivePolicy? ReadKeepalivePolicy(SqliteDataReader reader, int instancesOrdinal, int maxParallelismOrdinal, int headroomOrdinal)
    {
        if (reader.IsDBNull(instancesOrdinal) || reader.IsDBNull(maxParallelismOrdinal) || reader.IsDBNull(headroomOrdinal))
        {
            return null;
        }

        return NormalizeKeepalivePolicy(
            reader.GetInt32(instancesOrdinal),
            reader.GetInt32(maxParallelismOrdinal),
            reader.GetInt32(headroomOrdinal));
    }

    private static GroupClientKeepalivePolicy NormalizeKeepalivePolicy(
        int? instancesToKeepAlive,
        int? maxParallelismPerClient,
        int? parallelismHeadroom)
    {
        return new GroupClientKeepalivePolicy(
            Math.Max(1, instancesToKeepAlive ?? GroupClientKeepalivePolicy.Default.InstancesToKeepAlive),
            Math.Max(1, maxParallelismPerClient ?? GroupClientKeepalivePolicy.Default.MaxParallelismPerClient),
            Math.Max(1, parallelismHeadroom ?? GroupClientKeepalivePolicy.Default.ParallelismHeadroom));
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

    private static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"rl_{Base64UrlEncode(bytes)}";
    }

    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string GetKeyPrefix(string key) =>
        key.Length <= 12 ? key : key[..12];

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

    private sealed class KeyState
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
    DateTimeOffset? DisabledFromUtc,
    DateTimeOffset? DisabledUntilUtc,
    bool DisabledManually,
    string? DisabledReason)
{
    public static ClientAccess Enabled { get; } = new(false, null, null, false, null);
}

internal sealed record UserKeyInfo(
    string Id,
    string Name,
    string KeyPrefix,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastUsedUtc);

internal sealed record CreatedUserKey(
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
    string? UserKeyId,
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

internal sealed record TokenStatsByUserKey(
    string UserKeyId,
    string UserKeyName,
    string UserKeyPrefix,
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
    string? ClientPattern,
    GroupClientKeepalivePolicy? KeepalivePolicy = null);

internal sealed record GroupClientKeepalivePolicy(
    int InstancesToKeepAlive,
    int MaxParallelismPerClient,
    int ParallelismHeadroom)
{
    public static GroupClientKeepalivePolicy Default { get; } = new(1, 1, 1);
}

internal sealed record UserKeyGroupInfo(
    string UserKeyId,
    string UserKeyName,
    string UserKeyPrefix,
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
