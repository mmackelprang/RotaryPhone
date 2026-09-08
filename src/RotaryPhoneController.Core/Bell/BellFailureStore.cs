using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Bell;

/// <summary>
/// Durable home for <see cref="BellFailureRecord"/>s, keyed by phone id. Implementations must treat
/// a failed read or write as a non-event: losing this state costs one dismissed note, and nothing
/// on the ring path or the ack endpoint may be broken to protect it.
/// </summary>
public interface IBellFailureStore
{
    IReadOnlyDictionary<string, BellFailureRecord> Load();
    void Save(IReadOnlyDictionary<string, BellFailureRecord> records);
}

/// <summary>
/// A JSON file, following the <c>HT801ConfigService</c> precedent (<c>data/ht801-config.json</c>):
/// load on construct, save on mutate, one <c>Dictionary&lt;string, T&gt;</c> keyed by phone id.
///
/// <para>
/// <b>Why a file rather than SQLite.</b> The repo's other persistence precedent is
/// <c>SqliteCallHistoryService</c> (<c>data/call-history.db</c>), and it is the right tool for call
/// HISTORY — an append-only series that grows without bound and has to be trimmed to
/// MaxCallHistoryEntries. This is the opposite shape: at most one small immutable record per phone,
/// on an appliance that drives exactly one phone. There is nothing here to query, index, page or
/// trim. Adopting SQLite would mean a second database file, a connection held open for the process
/// lifetime, WAL sidecars and IDisposable plumbing through the tracker — all to store a single row.
/// Wrong weight for the problem.
/// </para>
///
/// <para>
/// <b>Why <c>data/</c>.</b> The deploy script rsyncs with <c>--exclude 'data/'</c>
/// (deploy/Deploy-ToLinux.ps1:90), so anything written there outlives a deploy. That exclusion is
/// what makes "survives a deploy" a fact rather than a hope; if it is ever removed, this guarantee
/// goes with it.
/// </para>
///
/// <para>
/// <b>One deliberate improvement on the precedent: the write is atomic.</b> HT801ConfigService does
/// a plain File.WriteAllText, which can leave a truncated file if the process dies mid-write. This
/// class exists BECAUSE a dismissal has to survive a crash — a crash-torn state file would defeat
/// the entire point of it — so it serializes to a sibling .tmp and then File.Move(overwrite: true),
/// which is a rename(2) on Linux and therefore all-or-nothing. A reader sees either the previous
/// complete file or the new complete file, never a half of one.
/// </para>
/// </summary>
public sealed class JsonBellFailureStore : IBellFailureStore
{
    // Shared by both directions so the on-disk shape can never drift between write and read.
    //
    // The enum goes to disk AS A STRING. An integer-valued Reason would be silently reinterpreted
    // the moment anyone inserts a member into BellFailureReason — a persisted `2` (Rejected today)
    // would come back as whatever landed in slot 2 next, and it would come back looking perfectly
    // valid. Strings survive a reordering; the cost is a handful of bytes.
    //
    // PropertyNameCaseInsensitive because BellFailureRecord is a positional record: deserialization
    // goes through its generated constructor, matching JSON names to constructor parameters, and
    // this removes any dependence on the casing convention in force when the file was written.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger? _logger;

    public JsonBellFailureStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, BellFailureRecord> Load()
    {
        // Missing file is the normal first-boot case, not a problem worth logging.
        if (!File.Exists(_path))
        {
            return new Dictionary<string, BellFailureRecord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(_path);
            var records = JsonSerializer.Deserialize<Dictionary<string, BellFailureRecord>>(json, JsonOptions);

            if (records is null)
            {
                return new Dictionary<string, BellFailureRecord>(StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, BellFailureRecord>(records, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // NEVER throw. This runs during construction of a singleton the whole server depends on,
            // so a corrupt or unreadable file here would mean an appliance that will not boot. The
            // cost of swallowing it is one forgotten dismissal; the cost of propagating it is the
            // phone. The next mutation overwrites the bad file, so this recovers rather than sticks.
            _logger?.LogWarning(ex,
                "Could not read bell-failure state from {Path} — starting empty. A dismissed note may reappear once.",
                _path);
            return new Dictionary<string, BellFailureRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, BellFailureRecord> records)
    {
        var tempPath = _path + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(tempPath, JsonSerializer.Serialize(records, JsonOptions));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Same rule as Load, for a sharper reason: this is called from inside the tracker's lock
            // on the ring path and from the ack endpoint. A full disk must not turn a bell failure
            // into an unhandled exception during an incoming call.
            _logger?.LogWarning(ex, "Could not persist bell-failure state to {Path}", _path);

            // A leftover .tmp is not itself harmful — nothing reads it — but it is the kind of
            // debris that makes a later reader wonder whether the real file is trustworthy.
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best effort. If we cannot even delete it, there is nothing useful left to try.
            }
        }
    }
}
