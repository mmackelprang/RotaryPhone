using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// On-disk cache for voicemail recordings (ADR §6.4). First request fetches from Google via the
/// recording fetcher and writes data/gv-voicemail-cache/{id}.bin; later requests serve the file so
/// RadioConsole never re-hits Google. Eviction by age (RetentionDays) and total size (MaxBytes).
/// The controller streams the file with range support so the &lt;audio&gt; scrubber works.
/// </summary>
public class GvVoicemailCache
{
    private readonly IGvRecordingFetcher _fetcher;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GvVoicemailCache> _logger;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly string _cacheDir;

    public GvVoicemailCache(IGvRecordingFetcher fetcher, IOptions<GVBridgeConfig> config,
        ILogger<GvVoicemailCache> logger)
    {
        _fetcher = fetcher;
        _config = config.Value;
        _logger = logger;
        // Resolve the cache dir to an ABSOLUTE path. PhysicalFileResult requires a rooted path and
        // throws NotSupportedException at request time on a relative one — and the config default
        // ("data/gv-voicemail-cache") is relative. Resolving here makes every served path absolute.
        _cacheDir = Path.IsPathRooted(_config.VoicemailCacheDir)
            ? _config.VoicemailCacheDir
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _config.VoicemailCacheDir));
    }

    private string PathFor(string voicemailId)
    {
        // Sanitize id → safe filename (GV ids are alnum + . ; keep it defensive). The appended ".bin"
        // suffix means even a ".."/"" id cannot become a traversal; an empty id falls back to a
        // stable placeholder so we never produce a bare ".bin".
        var safe = string.Concat(voicemailId.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        if (string.IsNullOrEmpty(safe)) safe = "_empty";
        return Path.Combine(_cacheDir, $"{safe}.bin");
    }

    /// <summary>
    /// Return the cache file path for a voicemail, fetching+writing on a miss. Null on fetch failure.
    /// </summary>
    public async Task<string?> GetOrFetchAsync(string voicemailId, string mediaRef, CancellationToken ct = default)
    {
        var path = PathFor(voicemailId);
        if (File.Exists(path))
            return path;

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path; // double-checked after lock

            var result = await _fetcher.FetchAsync(mediaRef, ct);
            if (!result.Success || result.Bytes is null)
                return null;

            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllBytesAsync(path, result.Bytes, ct);
            _logger.LogDebug("Cached voicemail {Id} ({Bytes} bytes)", voicemailId, result.Bytes.Length);
            Evict();
            return path;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Evict cache files older than RetentionDays, then oldest-first until under MaxBytes.
    /// NOT lock-protected: production calls it only from inside GetOrFetchAsync (under _fetchLock).
    /// Calling it concurrently with serving is tolerable — at worst a just-evicted file 404s, which
    /// the caller survives (the next request re-fetches). Public only for deterministic testing.
    /// </summary>
    public void Evict()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;

            var files = new DirectoryInfo(_cacheDir)
                .GetFiles("*.bin")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow.AddDays(-_config.VoicemailCacheRetentionDays);
            foreach (var f in files.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
            {
                TryDelete(f);
                files.Remove(f);
            }

            var total = files.Sum(f => f.Length);
            foreach (var f in files) // already oldest-first
            {
                if (total <= _config.VoicemailCacheMaxBytes) break;
                total -= f.Length;
                TryDelete(f);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voicemail cache eviction error");
        }
    }

    private void TryDelete(FileInfo f)
    {
        try { f.Delete(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete cache file {File}", f.Name); }
    }
}
