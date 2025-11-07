using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core.HT801;

/// <summary>
/// Service for managing HT801 configuration
/// Note: Full HT801 web API integration would require reverse engineering or official API docs
/// This implementation provides the UI and data management foundation
/// </summary>
public class HT801ConfigService : IHT801ConfigService
{
    private readonly ILogger<HT801ConfigService> _logger;
    private readonly Dictionary<string, HT801Config> _configs = new();
    private readonly object _lock = new();
    private readonly string? _storageFilePath;

    public HT801ConfigService(
        ILogger<HT801ConfigService> logger, 
        AppConfiguration appConfig,
        string? storageFilePath = null)
    {
        _logger = logger;
        _storageFilePath = storageFilePath;
        
        // Initialize configs from app configuration
        foreach (var phone in appConfig.Phones)
        {
            _configs[phone.Id] = new HT801Config
            {
                IpAddress = phone.HT801IpAddress,
                Extension = phone.HT801Extension,
                SipServerAddress = appConfig.SipListenAddress == "0.0.0.0" 
                    ? "192.168.1.100" 
                    : appConfig.SipListenAddress,
                SipServerPort = appConfig.SipPort
            };
        }
        
        // Load from file if it exists
        if (!string.IsNullOrEmpty(_storageFilePath) && File.Exists(_storageFilePath))
        {
            LoadFromFile();
        }
        
        _logger.LogInformation("HT801ConfigService initialized with {Count} phone configs", _configs.Count);
    }

    public HT801Config GetConfig(string phoneId)
    {
        lock (_lock)
        {
            if (_configs.TryGetValue(phoneId, out var config))
            {
                return config;
            }
            
            // Return default config if not found
            return new HT801Config();
        }
    }

    public void UpdateConfig(string phoneId, HT801Config config)
    {
        lock (_lock)
        {
            _configs[phoneId] = config;
            SaveToFile();
        }
        
        _logger.LogInformation("HT801 config updated for phone: {PhoneId}", phoneId);
    }

    public async Task<HT801ConnectionTestResult> TestConnectionAsync(string ipAddress)
    {
        try
        {
            _logger.LogInformation("Testing connection to HT801 at {IpAddress}", ipAddress);
            
            // Test 1: Ping the device
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, 3000);
            
            if (reply.Status != IPStatus.Success)
            {
                return new HT801ConnectionTestResult
                {
                    Success = false,
                    Message = $"Device not reachable: {reply.Status}"
                };
            }
            
            _logger.LogInformation("HT801 at {IpAddress} responded to ping in {RoundtripTime}ms", 
                ipAddress, reply.RoundtripTime);
            
            // Test 2: Try to connect to common HT801 web interface port (80)
            // Note: Full implementation would use HttpClient to check web interface
            // For now, we just verify ping works
            
            return new HT801ConnectionTestResult
            {
                Success = true,
                Message = $"Device reachable (ping: {reply.RoundtripTime}ms)",
                DeviceInfo = "HT801 ATA",
                FirmwareVersion = "Unknown (web API not implemented)"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test connection to {IpAddress}", ipAddress);
            return new HT801ConnectionTestResult
            {
                Success = false,
                Message = $"Connection test failed: {ex.Message}"
            };
        }
    }

    public async Task<bool> ApplyConfigToDeviceAsync(HT801Config config)
    {
        // TODO: Future enhancement - implement HT801 web API integration
        // The HT801 has a web interface at http://<ip>/cgi-bin/dologin
        // Would need to:
        // 1. Authenticate with admin credentials
        // 2. POST configuration changes
        // 3. Reboot device if needed
        
        _logger.LogWarning("ApplyConfigToDeviceAsync not yet implemented - manual configuration required");
        await Task.CompletedTask;
        return false;
    }

    public async Task<HT801Config?> ReadConfigFromDeviceAsync(string ipAddress)
    {
        // TODO: Future enhancement - implement HT801 web API integration
        // Would need to:
        // 1. Authenticate with admin credentials
        // 2. GET current configuration from device
        // 3. Parse response and map to HT801Config object
        
        _logger.LogWarning("ReadConfigFromDeviceAsync not yet implemented - manual configuration required");
        await Task.CompletedTask;
        return null;
    }

    private void LoadFromFile()
    {
        if (string.IsNullOrEmpty(_storageFilePath))
            return;
            
        try
        {
            var json = File.ReadAllText(_storageFilePath);
            var configs = JsonSerializer.Deserialize<Dictionary<string, HT801Config>>(json);
            
            if (configs != null)
            {
                lock (_lock)
                {
                    foreach (var kvp in configs)
                    {
                        _configs[kvp.Key] = kvp.Value;
                    }
                }
                _logger.LogInformation("Loaded HT801 configs from {Path}", _storageFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load HT801 configs from {Path}", _storageFilePath);
        }
    }

    private void SaveToFile()
    {
        if (string.IsNullOrEmpty(_storageFilePath))
            return;
            
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_configs, options);
            
            var directory = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.WriteAllText(_storageFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save HT801 configs to {Path}", _storageFilePath);
        }
    }
}
