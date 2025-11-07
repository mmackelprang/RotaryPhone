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
    public string HT801IpAddress { get; set; } = "192.168.1.10";
    
    /// <summary>
    /// SIP extension to ring on the HT801
    /// </summary>
    public string HT801Extension { get; set; } = "1000";
    
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
    /// Enable actual RTP audio bridge implementation (vs mock)
    /// </summary>
    public bool UseActualRtpAudioBridge { get; set; } = false;
    
    /// <summary>
    /// List of configured rotary phones
    /// </summary>
    public List<RotaryPhoneConfig> Phones { get; set; } = new()
    {
        new RotaryPhoneConfig()
    };
}
