namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// GV thread "folder"/filter selector for api2thread/list.
/// The Sms / Calls / Voicemail wire values are VERIFIED against a live capture (2026-07-31): the
/// request body's folder field is echoed back on every thread at thread[5] = [1, folder], and the
/// checked-in captures assert that round-trip.
/// <para>
/// <see cref="All"/> is still UNVERIFIED — no capture was taken for it. It therefore has NO wire
/// value; <see cref="GvThreadFolderExtensions.ToWireValue"/> throws rather than guessing, because
/// guessing wrong here silently returns the WRONG FOLDER's records (exactly the defect that made
/// voicemail queries return call logs).
/// </para>
/// </summary>
public enum GvThreadFolder
{
    /// <summary>UNVERIFIED — has no wire value. Calling ToWireValue() on this throws.</summary>
    All,
    Sms,
    Calls,
    Voicemail
}

public static class GvThreadFolderExtensions
{
    /// <summary>
    /// Map a folder to its api2thread/list request wire value.
    /// VERIFIED (2026-07-31 capture): Sms=2, Calls=3, Voicemail=4.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown for <see cref="GvThreadFolder.All"/>, whose wire value has never been captured.
    /// Fail loudly instead of defaulting — a wrong folder integer returns another folder's data
    /// with a 200 OK, which is indistinguishable from success at every layer above.
    /// </exception>
    public static int ToWireValue(this GvThreadFolder folder) => folder switch
    {
        GvThreadFolder.Sms => 2,
        GvThreadFolder.Calls => 3,
        GvThreadFolder.Voicemail => 4,
        GvThreadFolder.All => throw new NotSupportedException(
            "GvThreadFolder.All has no verified api2thread/list wire value. Capture it live before " +
            "using it — an unverified folder integer silently returns a different folder's records."),
        _ => throw new NotSupportedException($"Unknown GvThreadFolder '{folder}'.")
    };

    /// <summary>True when the folder has a capture-verified wire value.</summary>
    public static bool IsVerified(this GvThreadFolder folder) =>
        folder is GvThreadFolder.Sms or GvThreadFolder.Calls or GvThreadFolder.Voicemail;
}
