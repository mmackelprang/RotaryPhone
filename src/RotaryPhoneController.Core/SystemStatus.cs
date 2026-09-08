namespace RotaryPhoneController.Core;

/// <summary>
/// Represents the current system status including platform, Bluetooth, and SIP information
/// </summary>
public class SystemStatus
{
    /// <summary>
    /// Current platform (Windows, Linux, Unknown)
    /// </summary>
    public string Platform { get; set; } = "Unknown";

    /// <summary>
    /// Whether the device is a Raspberry Pi
    /// </summary>
    public bool IsRaspberryPi { get; set; }

    /// <summary>
    /// Whether Bluetooth HFP is enabled in configuration
    /// </summary>
    public bool BluetoothEnabled { get; set; }

    /// <summary>
    /// Whether a Bluetooth device is currently connected
    /// </summary>
    public bool BluetoothConnected { get; set; }

    /// <summary>
    /// MAC address of the connected Bluetooth device (if any)
    /// </summary>
    public string? BluetoothDeviceAddress { get; set; }

    /// <summary>
    /// Whether the SIP server is listening for connections
    /// </summary>
    public bool SipListening { get; set; }

    /// <summary>
    /// SIP server listen address
    /// </summary>
    public string? SipListenAddress { get; set; }

    /// <summary>
    /// SIP server listen port
    /// </summary>
    public int SipPort { get; set; }

    /// <summary>
    /// The address the reachability probe was aimed at: the <b>RESOLVED</b> registrar binding —
    /// where an INVITE would actually go — and never the configured address.
    /// <para>
    /// Those are different values and they can disagree. The configured address reported correct for
    /// the entire 2026-07 outage while every INVITE went somewhere else, which is why this field
    /// deliberately no longer carries it (changed 2026-09-08).
    /// </para>
    /// <para>
    /// <c>null</c> means NOT YET PROBED — the same tri-state as <see cref="Ht801Reachable"/> and
    /// <see cref="Ht801LastCheckedUtc"/> below, and it moves with them: all three are null together
    /// before the first probe resolves an address, and all three are populated together afterwards.
    /// Render null as "Unknown", never as "no HT801 configured".
    /// </para>
    /// </summary>
    public string? Ht801IpAddress { get; set; }

    /// <summary>
    /// Whether the HT801 is reachable (pingable).
    /// <para>
    /// <c>null</c> means NOT YET PROBED or CANNOT DETERMINE — it does NOT mean offline. Consumers
    /// must render null as "Unknown", never as "Offline". Coercing unknown to false is how a healthy
    /// device gets reported as dead (and, worse, how a dead one gets reported as merely unknown when
    /// the probe is skipped). Pair with <see cref="Ht801LastCheckedUtc"/> to tell the two apart.
    /// </para>
    /// </summary>
    public bool? Ht801Reachable { get; set; }

    /// <summary>
    /// When the HT801 reachability probe last ran. Null means never probed in this process —
    /// which, combined with a null <see cref="Ht801Reachable"/>, means genuinely UNKNOWN, not offline.
    /// </summary>
    public DateTime? Ht801LastCheckedUtc { get; set; }
}
