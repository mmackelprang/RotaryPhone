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
/// class exists BECAUSE a dismissal has to survive a restart — a torn state file would defeat the
/// entire point of it — so it serializes to a sibling temp file and then File.Move(overwrite: true),
/// which is a rename(2) on Linux and therefore all-or-nothing. A reader sees either the previous
/// complete file or the new complete file, never a half of one.
/// </para>
///
/// <para>
/// <b>Atomicity is not durability, and the difference is worth stating precisely.</b> What
/// WriteAllText + Move buys is that the file is never observed half-written. It does NOT buy a
/// guaranteed flush to the platter: nothing here calls fsync on either the file or the directory.
/// So a PROCESS CRASH or a normal service restart is fully covered — the page cache outlives the
/// process — and that is the case this feature was built for. A POWER LOSS is best-effort: on ext4
/// the auto_da_alloc heuristic makes a rename-over-existing-file case commit the data first in
/// practice, but that is a mount-option-dependent behaviour, not a promise. The cost of losing the
/// file in a power cut is one dismissed note reappearing, which does not justify an fsync on the
/// ring path.
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
    // They survive an UNKNOWN MEMBER too, but only because of the custom converter below rather than
    // the string encoding itself. JsonStringEnumConverter throws on a name it does not recognise,
    // and Load's catch-all would then discard the ENTIRE file — every phone's state, including a
    // dismissal — over one unreadable field. That is a real rollback scenario: a build that adds a
    // BellFailureReason member writes the new name, and the older build you roll back to cannot read
    // it. Mapping the unrecognised name to Unknown keeps the rest of the record intact.
    //
    // Still NOT covered, deliberately: a MISSING field degrades to its CLR default rather than being
    // detected, so an older build reading a newer file that omits Acknowledged would read it as
    // false. No shipped build writes a record without those fields, so this is a hazard to know
    // about rather than one to guard against today.
    //
    // PropertyNameCaseInsensitive because BellFailureRecord is a positional record: deserialization
    // goes through its generated constructor, matching JSON names to constructor parameters, and
    // this removes any dependence on the casing convention in force when the file was written.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new UnknownTolerantReasonConverter() }
    };

    /// <summary>
    /// Writes <see cref="BellFailureReason"/> as its name, and reads any name it does not recognise
    /// as <see cref="BellFailureReason.Unknown"/> rather than throwing.
    ///
    /// <para>
    /// <c>Unknown</c> exists precisely as the unrecognised-value bucket — BellFailureReason's own
    /// summary says Radio.Web "treats an unrecognised value as Unknown", so this makes the store
    /// agree with the contract the enum already advertises. Anything that is not a recognised name
    /// lands there: an unknown string, a number, an explicit null. The alternative is a JsonException
    /// that costs the whole file.
    /// </para>
    /// </summary>
    private sealed class UnknownTolerantReasonConverter : JsonConverter<BellFailureReason>
    {
        public override BellFailureReason Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString();

                // Enum.IsDefined rejects a numeric string that parses to an undefined value — e.g.
                // "99" would otherwise become (BellFailureReason)99 and pass for a real member.
                if (name is not null
                    && Enum.TryParse<BellFailureReason>(name, ignoreCase: true, out var parsed)
                    && Enum.IsDefined(parsed))
                {
                    return parsed;
                }
            }

            // Any other token (number, null, ...) is left unconsumed on purpose — the serializer
            // skips the value for us — and reported as Unknown.
            return BellFailureReason.Unknown;
        }

        public override void Write(Utf8JsonWriter writer, BellFailureReason value,
            JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

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

            // Deserialized as NULLABLE values on purpose. `{"default": null}` is valid JSON and
            // produces a present key with a null value — the static type says that cannot happen,
            // the runtime disagrees, and nothing above would catch it. Filtering here fixes it at
            // the source: the tracker dereferences these records on the ring path and from the ack
            // endpoint, so a null that got through would surface as an NRE during an incoming call.
            var records = JsonSerializer.Deserialize<Dictionary<string, BellFailureRecord?>>(json, JsonOptions);

            if (records is null)
            {
                return new Dictionary<string, BellFailureRecord>(StringComparer.OrdinalIgnoreCase);
            }

            return records
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
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
        // Declared out here so the catch can clean it up; assigned inside the try so that nothing on
        // the way to it can throw past the guard.
        string? tempPath = null;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // A RANDOM temp name in the same directory, not a fixed "<path>.tmp". Two processes over
            // the same data/ directory — a manual `dotnet run` alongside the installed service, or
            // an overlapping restart — would otherwise share one scratch file and could interleave a
            // write with the other's rename, silently publishing a mix or losing an update. The
            // rename is atomic either way; what the fixed name lost was the guarantee that the bytes
            // being renamed are the ones THIS call wrote. Same directory is required, not incidental:
            // rename(2) is only atomic within a filesystem.
            tempPath = Path.Combine(
                string.IsNullOrEmpty(directory) ? "." : directory,
                Path.GetRandomFileName());

            File.WriteAllText(tempPath, JsonSerializer.Serialize(records, JsonOptions));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Same rule as Load, for a sharper reason: this is called from inside the tracker's lock
            // on the ring path and from the ack endpoint. A full disk must not turn a bell failure
            // into an unhandled exception during an incoming call.
            _logger?.LogWarning(ex, "Could not persist bell-failure state to {Path}", _path);

            // A leftover temp file is not itself harmful — nothing reads it — but it is the kind of
            // debris that makes a later reader wonder whether the real file is trustworthy.
            try
            {
                if (tempPath is not null && File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best effort. If we cannot even delete it, there is nothing useful left to try.
            }
        }
    }
}
