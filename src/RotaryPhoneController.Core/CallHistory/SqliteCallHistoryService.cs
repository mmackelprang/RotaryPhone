using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.CallHistory;

/// <summary>
/// SQLite-backed implementation of call history service
/// </summary>
public class SqliteCallHistoryService : ICallHistoryService, IDisposable
{
    private readonly ILogger<SqliteCallHistoryService> _logger;
    private readonly int _maxEntries;
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public event Action<CallHistoryEntry>? OnCallHistoryAdded;

    public SqliteCallHistoryService(ILogger<SqliteCallHistoryService> logger, string dbPath, int maxEntries = 100)
    {
        _logger = logger;
        _maxEntries = maxEntries;

        // Ensure the parent directory exists for file-backed databases
        if (dbPath != ":memory:")
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        // Open a single shared connection and keep it open for the lifetime of the
        // service. Keeping the connection open is what keeps an in-memory database alive.
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using (var create = _connection.CreateCommand())
        {
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS CallHistory (
                    Id TEXT PRIMARY KEY,
                    PhoneNumber TEXT NOT NULL,
                    CallerName TEXT,
                    Direction TEXT NOT NULL,
                    AnsweredOn TEXT NOT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    PhoneId TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_CallHistory_StartTime ON CallHistory(StartTime);
                CREATE INDEX IF NOT EXISTS IX_CallHistory_PhoneId ON CallHistory(PhoneId);";
            create.ExecuteNonQuery();
        }

        _logger.LogInformation(
            "SqliteCallHistoryService initialized with max entries: {MaxEntries}, db: {DbPath}",
            maxEntries,
            dbPath);
    }

    public void AddCallHistory(CallHistoryEntry entry)
    {
        lock (_lock)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO CallHistory (Id, PhoneNumber, CallerName, Direction, AnsweredOn, StartTime, EndTime, PhoneId)
                    VALUES (@id, @number, @callerName, @direction, @answeredOn, @startTime, @endTime, @phoneId);";
                cmd.Parameters.AddWithValue("@id", entry.Id.ToString());
                cmd.Parameters.AddWithValue("@number", entry.PhoneNumber);
                cmd.Parameters.AddWithValue("@callerName", entry.CallerName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@direction", entry.Direction.ToString());
                cmd.Parameters.AddWithValue("@answeredOn", entry.AnsweredOn.ToString());
                cmd.Parameters.AddWithValue("@startTime", entry.StartTime.ToString("O"));
                cmd.Parameters.AddWithValue("@endTime", entry.EndTime?.ToString("O") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@phoneId", entry.PhoneId);
                cmd.ExecuteNonQuery();
            }

            _logger.LogInformation(
                "Call history added: {Direction} call {Number} on {PhoneId} at {Time}",
                entry.Direction,
                entry.PhoneNumber,
                entry.PhoneId,
                entry.StartTime);

            // Trim history if needed (keep only the newest _maxEntries by StartTime)
            if (_maxEntries > 0)
            {
                using var trim = _connection.CreateCommand();
                trim.CommandText = @"
                    DELETE FROM CallHistory WHERE Id NOT IN (
                        SELECT Id FROM CallHistory ORDER BY StartTime DESC LIMIT @max
                    );";
                trim.Parameters.AddWithValue("@max", _maxEntries);
                var trimmed = trim.ExecuteNonQuery();
                if (trimmed > 0)
                    _logger.LogDebug("Trimmed {Count} old entries from call history", trimmed);
            }

            OnCallHistoryAdded?.Invoke(entry);
        }
    }

    public void UpdateCallHistory(CallHistoryEntry entry)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE CallHistory SET
                    PhoneNumber = @number,
                    CallerName = @callerName,
                    Direction = @direction,
                    AnsweredOn = @answeredOn,
                    StartTime = @startTime,
                    EndTime = @endTime,
                    PhoneId = @phoneId
                WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", entry.Id.ToString());
            cmd.Parameters.AddWithValue("@number", entry.PhoneNumber);
            cmd.Parameters.AddWithValue("@callerName", entry.CallerName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@direction", entry.Direction.ToString());
            cmd.Parameters.AddWithValue("@answeredOn", entry.AnsweredOn.ToString());
            cmd.Parameters.AddWithValue("@startTime", entry.StartTime.ToString("O"));
            cmd.Parameters.AddWithValue("@endTime", entry.EndTime?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@phoneId", entry.PhoneId);

            var affected = cmd.ExecuteNonQuery();
            if (affected == 0)
            {
                _logger.LogWarning("Attempted to update non-existent call history entry: {Id}", entry.Id);
            }
            else
            {
                _logger.LogInformation(
                    "Call history updated: {Id} - Duration: {Duration}",
                    entry.Id,
                    entry.Duration);
            }
        }
    }

    public IEnumerable<CallHistoryEntry> GetCallHistory(int maxEntries = 0)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = maxEntries > 0
                ? "SELECT Id, PhoneNumber, CallerName, Direction, AnsweredOn, StartTime, EndTime, PhoneId FROM CallHistory ORDER BY StartTime DESC LIMIT @max;"
                : "SELECT Id, PhoneNumber, CallerName, Direction, AnsweredOn, StartTime, EndTime, PhoneId FROM CallHistory ORDER BY StartTime DESC;";
            if (maxEntries > 0)
                cmd.Parameters.AddWithValue("@max", maxEntries);

            var results = new List<CallHistoryEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadEntry(reader));
            return results;
        }
    }

    public IEnumerable<CallHistoryEntry> GetCallHistoryForPhone(string phoneId, int maxEntries = 0)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = maxEntries > 0
                ? "SELECT Id, PhoneNumber, CallerName, Direction, AnsweredOn, StartTime, EndTime, PhoneId FROM CallHistory WHERE PhoneId = @phoneId ORDER BY StartTime DESC LIMIT @max;"
                : "SELECT Id, PhoneNumber, CallerName, Direction, AnsweredOn, StartTime, EndTime, PhoneId FROM CallHistory WHERE PhoneId = @phoneId ORDER BY StartTime DESC;";
            cmd.Parameters.AddWithValue("@phoneId", phoneId);
            if (maxEntries > 0)
                cmd.Parameters.AddWithValue("@max", maxEntries);

            var results = new List<CallHistoryEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadEntry(reader));
            return results;
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM CallHistory;";
            var count = cmd.ExecuteNonQuery();
            _logger.LogInformation("Cleared {Count} entries from call history", count);
        }
    }

    public int GetCallCount()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM CallHistory;";
            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result);
        }
    }

    /// <summary>
    /// Materializes a <see cref="CallHistoryEntry"/> from the current reader row.
    /// Duration is a computed property and is not read from the database.
    /// </summary>
    private static CallHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        return new CallHistoryEntry
        {
            Id = Guid.Parse(reader.GetString(0)),
            PhoneNumber = reader.GetString(1),
            CallerName = reader.IsDBNull(2) ? null : reader.GetString(2),
            Direction = Enum.Parse<CallDirection>(reader.GetString(3)),
            AnsweredOn = Enum.Parse<CallAnsweredOn>(reader.GetString(4)),
            StartTime = DateTime.Parse(reader.GetString(5)),
            EndTime = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
            PhoneId = reader.GetString(7)
        };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connection.Close();
            _connection.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
