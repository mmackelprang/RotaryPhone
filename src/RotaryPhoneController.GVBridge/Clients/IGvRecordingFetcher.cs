namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>Result of fetching a recording's bytes from Google.</summary>
public record GvRecordingFetchResult(bool Success, byte[]? Bytes, string ContentType);

/// <summary>
/// Seam isolating the UNVERIFIED voicemail media-fetch shape (ADR §3.2, §11 step 3: the recording
/// may be recording/get?id=… OR an embedded media URL). Keeping it behind this interface means the
/// live correction is a one-file change in <see cref="GvRecordingFetcher"/>; the cache + controller
/// never learn Google's media URL form.
/// </summary>
public interface IGvRecordingFetcher
{
    /// <summary>Fetch the recording bytes for a media reference (id or URL from the list response).</summary>
    Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default);
}
