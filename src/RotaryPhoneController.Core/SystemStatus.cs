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
    /// Configured IP address of the HT801 device
    /// </summary>
    public string? Ht801IpAddress { get; set; }

    /// <summary>
    /// Whether the HT801 is reachable (pingable)
    /// </summary>
    public bool? Ht801Reachable { get; set; }
}
