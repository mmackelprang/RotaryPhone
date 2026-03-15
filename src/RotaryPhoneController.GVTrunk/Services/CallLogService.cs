using Microsoft.Data.Sqlite;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Services;

public class CallLogService : ICallLogService
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SqliteConnection? _connection;

    public CallLogService(string dbPath)
    {
        _connectionString = dbPath == ":memory:"
            ? "Data Source=:memory:"
            : $"Data Source={dbPath}";
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS CallLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT,
                Direction TEXT NOT NULL,
                RemoteNumber TEXT NOT NULL,
                Status TEXT NOT NULL,
                DurationSeconds INTEGER,
                Notes TEXT
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> AddEntryAsync(CallLogEntry entry)
    {
        if (_connection == null) throw new InvalidOperationException("Not initialized");

        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO CallLog (StartedAt, EndedAt, Direction, RemoteNumber, Status, DurationSeconds, Notes)
                VALUES (@started, @ended, @dir, @number, @status, @duration, @notes);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@started", entry.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@ended", entry.EndedAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@dir", entry.Direction);
            cmd.Parameters.AddWithValue("@number", entry.RemoteNumber);
            cmd.Parameters.AddWithValue("@status", entry.Status);
            cmd.Parameters.AddWithValue("@duration", entry.DurationSeconds ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@notes", entry.Notes ?? (object)DBNull.Value);

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateEntryAsync(int id, DateTime endedAt, string status, int durationSeconds)
    {
        if (_connection == null) throw new InvalidOperationException("Not initialized");

        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE CallLog SET EndedAt = @ended, Status = @status, DurationSeconds = @duration
                WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@ended", endedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@duration", durationSeconds);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CallLogEntry>> GetRecentAsync(int count = 50)
    {
        if (_connection == null) throw new InvalidOperationException("Not initialized");

        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Id, StartedAt, EndedAt, Direction, RemoteNumber, Status, DurationSeconds, Notes FROM CallLog ORDER BY StartedAt DESC LIMIT @count";
            cmd.Parameters.AddWithValue("@count", count);

            var entries = new List<CallLogEntry>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(new CallLogEntry(
                    reader.GetInt32(0),
                    DateTime.Parse(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)
                ));
            }
            return entries;
        }
        finally
        {
            _lock.Release();
        }
    }
}
