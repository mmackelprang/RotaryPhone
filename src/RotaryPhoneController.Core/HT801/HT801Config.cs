namespace RotaryPhoneController.Core.HT801;

/// <summary>
/// Configuration settings for the Grandstream HT801 ATA
/// </summary>
public class HT801Config
{
    /// <summary>
    /// IP address of the HT801
    /// </summary>
    public string IpAddress { get; set; } = "192.168.1.10";
    
    /// <summary>
    /// Admin username for web interface
    /// </summary>
    public string AdminUsername { get; set; } = "admin";
    
    /// <summary>
    /// Admin password for web interface
    /// </summary>
    public string AdminPassword { get; set; } = string.Empty;
    
    /// <summary>
    /// SIP extension number
    /// </summary>
    public string Extension { get; set; } = "1000";
    
    /// <summary>
    /// Primary SIP Server (Raspberry Pi IP)
    /// </summary>
    public string SipServerAddress { get; set; } = "192.168.1.100";
    
    /// <summary>
    /// SIP server port
    /// </summary>
    public int SipServerPort { get; set; } = 5060;
    
    /// <summary>
    /// Enable pulse dialing (for rotary phones)
    /// </summary>
    public bool PulseDialing { get; set; } = true;
    
    /// <summary>
    /// Pulse rate (pulses per second, typically 10)
    /// </summary>
    public int PulseRate { get; set; } = 10;
    
    /// <summary>
    /// Enable call waiting
    /// </summary>
    public bool CallWaiting { get; set; } = false;
    
    /// <summary>
    /// Preferred codec (typically PCMU)
    /// </summary>
    public string PreferredCodec { get; set; } = "PCMU";
    
    /// <summary>
    /// Hook flash timing (milliseconds)
    /// </summary>
    public int HookFlashTiming { get; set; } = 300;
    
    /// <summary>
    /// Ring voltage level (0-5, where 5 is highest)
    /// </summary>
    public int RingVoltage { get; set; } = 3;
}

/// <summary>
/// Response from connection test
/// </summary>
public class HT801ConnectionTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? DeviceInfo { get; set; }
    public string? FirmwareVersion { get; set; }
}
