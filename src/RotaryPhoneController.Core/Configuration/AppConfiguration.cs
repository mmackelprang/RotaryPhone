namespace RotaryPhoneController.Core.Configuration;

/// <summary>
/// Configuration for a single rotary phone instance
/// </summary>
public class RotaryPhoneConfig
{
    /// <summary>
    /// Unique identifier for this phone
    /// </summary>
    public string Id { get; set; } = "default";
    
    /// <summary>
    /// Friendly name for this phone
    /// </summary>
    public string Name { get; set; } = "Rotary Phone";
    
    /// <summary>
    /// IP address of the HT801 ATA device
    /// </summary>
    public string HT801IpAddress { get; set; } = "192.168.86.250";
    
    /// <summary>
    /// SIP extension to ring on the HT801
    /// </summary>
    public string HT801Extension { get; set; } = "1000";

    /// <summary>
    /// Admin password for HT801 web interface
    /// </summary>
    public string HT801AdminPassword { get; set; } = "";

    /// <summary>
    /// Bluetooth MAC address of the paired mobile phone (optional)
    /// </summary>
    public string? BluetoothMacAddress { get; set; }
}

/// <summary>
/// Main application configuration
/// </summary>
public class AppConfiguration
{
    /// <summary>
    /// SIP server listening IP address
    /// </summary>
    public string SipListenAddress { get; set; } = "0.0.0.0";
    
    /// <summary>
    /// SIP server listening port
    /// </summary>
    public int SipPort { get; set; } = 5060;
    
    /// <summary>
    /// RTP base port for audio streaming
    /// </summary>
    public int RtpBasePort { get; set; } = 49000;
    
    /// <summary>
    /// Enable call history logging
    /// </summary>
    public bool EnableCallHistory { get; set; } = true;
    
    /// <summary>
    /// Maximum number of call history entries to keep
    /// </summary>
    public int MaxCallHistoryEntries { get; set; } = 100;
    
    /// <summary>
    /// Bluetooth device advertised name (shown to phones when pairing)
    /// </summary>
    public string BluetoothDeviceName { get; set; } = "Rotary Phone";
    
    /// <summary>
    /// Enable actual Bluetooth HFP implementation (vs mock)
    /// </summary>
    public bool UseActualBluetoothHfp { get; set; } = false;

    /// <summary>
    /// BlueZ adapter to use (e.g., "hci1"). Null = default adapter.
    /// </summary>
    public string? BluetoothAdapter { get; set; }

    /// <summary>
    /// Alias to set on the BT adapter (visible name during pairing).
    /// </summary>
    public string BluetoothAdapterAlias { get; set; } = "Rotary Phone";

    /// <summary>
    /// Maximum number of phones that can be connected simultaneously.
    /// </summary>
    public int MaxConnectedPhones { get; set; } = 2;

    /// <summary>
    /// Base UDP port for SCO audio bridge (per-device: base, base+1; base+2, base+3; ...).
    /// </summary>
    public int ScoUdpBasePort { get; set; } = 49100;

    /// <summary>
    /// Force a specific platform for Bluetooth adapter selection.
    /// Values: "Windows", "Linux", or null/empty for auto-detect.
    /// Useful for testing platform-specific code paths.
    /// </summary>
    public string? ForcePlatform { get; set; } = null;

    /// <summary>
    /// Enable actual RTP audio bridge implementation (vs mock)
    /// </summary>
    public bool UseActualRtpAudioBridge { get; set; } = false;
    
    /// <summary>
    /// Enable contact list feature
    /// </summary>
    public bool EnableContacts { get; set; } = true;
    
    /// <summary>
    /// Path to store contacts JSON file (relative to app directory)
    /// </summary>
    public string ContactsStoragePath { get; set; } = "data/contacts.json";
    
    /// <summary>
    /// List of configured rotary phones
    /// </summary>
    public List<RotaryPhoneConfig> Phones { get; set; } = new()
    {
        new RotaryPhoneConfig()
    };
}
