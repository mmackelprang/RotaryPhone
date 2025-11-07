namespace RotaryPhoneController.Core.HT801;

/// <summary>
/// Service interface for managing HT801 configuration
/// </summary>
public interface IHT801ConfigService
{
    /// <summary>
    /// Get the current HT801 configuration for a phone
    /// </summary>
    /// <param name="phoneId">Phone identifier</param>
    /// <returns>HT801 configuration</returns>
    HT801Config GetConfig(string phoneId);
    
    /// <summary>
    /// Update the HT801 configuration for a phone
    /// </summary>
    /// <param name="phoneId">Phone identifier</param>
    /// <param name="config">New configuration</param>
    void UpdateConfig(string phoneId, HT801Config config);
    
    /// <summary>
    /// Test connection to HT801 device
    /// </summary>
    /// <param name="ipAddress">IP address to test</param>
    /// <returns>Connection test result</returns>
    Task<HT801ConnectionTestResult> TestConnectionAsync(string ipAddress);
    
    /// <summary>
    /// Apply configuration to HT801 device (future enhancement)
    /// This would use the HT801 web API to push configuration changes
    /// </summary>
    /// <param name="config">Configuration to apply</param>
    /// <returns>True if successful</returns>
    Task<bool> ApplyConfigToDeviceAsync(HT801Config config);
    
    /// <summary>
    /// Read current configuration from HT801 device (future enhancement)
    /// This would use the HT801 web API to read current settings
    /// </summary>
    /// <param name="ipAddress">IP address of device</param>
    /// <returns>Current configuration from device</returns>
    Task<HT801Config?> ReadConfigFromDeviceAsync(string ipAddress);
}
