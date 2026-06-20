namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// GV thread "folder"/filter selector for api2thread/list.
/// The integer WIRE VALUES are UNVERIFIED — ADR §11 step 1 must confirm the folder enum for
/// SMS/inbox vs voicemail. These are best-known placeholders; keep the mapping in ONE place
/// (<see cref="GvThreadFolderExtensions.ToWireValue"/>) so a live correction is a one-line change.
/// </summary>
public enum GvThreadFolder
{
    All,
    Sms,
    Voicemail
}

public static class GvThreadFolderExtensions
{
    /// <summary>
    /// Map a folder to its api2thread/list request wire value.
    /// UNVERIFIED — ADR §11 step 1. The web client loads each tab (All / Voicemail / Recorded /
    /// Missed) with a folder enum; the exact integers are pinned during live capture.
    /// </summary>
    public static int ToWireValue(this GvThreadFolder folder) => folder switch
    {
        GvThreadFolder.All => 1,
        GvThreadFolder.Sms => 2,
        GvThreadFolder.Voicemail => 3,
        _ => 1
    };
}
