using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    private readonly AppConfiguration _appConfig;
    private readonly Dictionary<string, HT801Config> _configs = new();
    private readonly object _lock = new();
    private readonly string? _storageFilePath;

    public HT801ConfigService(
        ILogger<HT801ConfigService> logger,
        AppConfiguration appConfig,
        string? storageFilePath = null)
    {
        _logger = logger;
        _appConfig = appConfig;
        _storageFilePath = storageFilePath;
        
        // Initialize configs from app configuration
        foreach (var phone in appConfig.Phones)
        {
            _configs[phone.Id] = new HT801Config
            {
                IpAddress = phone.HT801IpAddress,
                Extension = phone.HT801Extension,
                AdminPassword = phone.HT801AdminPassword,
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
            _logger.LogDebug("Testing connection to HT801 at {IpAddress}", ipAddress);
            
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
            
            _logger.LogDebug("HT801 at {IpAddress} responded to ping in {RoundtripTime}ms",
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
        using var api = new HT801ApiClient(_logger);
        if (!await api.LoginAsync(config.IpAddress, config.AdminUsername, config.AdminPassword))
            return false;

        var serverIp = GetServerIpForDevice(config.IpAddress);
        var values = new Dictionary<string, string>
        {
            [HT801ApiClient.PSipServer] = serverIp,
            [HT801ApiClient.PSipUserId] = config.Extension,
            [HT801ApiClient.PSipPort] = config.SipServerPort.ToString()
        };

        return await api.SetValuesAsync(config.IpAddress, values);
    }

    public async Task<HT801Config?> ReadConfigFromDeviceAsync(string ipAddress)
    {
        HT801Config? foundConfig;
        lock (_lock)
        {
            foundConfig = _configs.Values.FirstOrDefault(c => c.IpAddress == ipAddress);
        }

        var username = foundConfig?.AdminUsername ?? "admin";
        var password = foundConfig?.AdminPassword ?? "";

        using var api = new HT801ApiClient(_logger);
        if (!await api.LoginAsync(ipAddress, username, password))
            return null;

        var sipServer = await api.GetValueAsync(ipAddress, HT801ApiClient.PSipServer);
        var extension = await api.GetValueAsync(ipAddress, HT801ApiClient.PSipUserId);
        var port = await api.GetValueAsync(ipAddress, HT801ApiClient.PSipPort);
        var firmware = await api.GetValueAsync(ipAddress, HT801ApiClient.PFirmwareVersion);

        return new HT801Config
        {
            IpAddress = ipAddress,
            SipServerAddress = sipServer ?? "",
            Extension = extension ?? "1000",
            SipServerPort = int.TryParse(port, out var p) ? p : 5060,
            AdminUsername = username,
            AdminPassword = password
        };
    }

    public async Task<HT801ValidationResult> ValidateDeviceAsync(string phoneId, bool autoFix = false)
    {
        var result = new HT801ValidationResult();
        var config = GetConfig(phoneId);

        using var api = new HT801ApiClient(_logger);

        // Step 1: Login
        if (!await api.LoginAsync(config.IpAddress, config.AdminUsername, config.AdminPassword))
        {
            result.LoginSucceeded = false;
            result.Items.Add(new HT801ValidationItem
            {
                Setting = "Login",
                Expected = "success",
                Actual = "failed",
                Match = false
            });
            return result;
        }

        result.LoginSucceeded = true;

        // Step 2: Get product model and firmware
        result.ProductModel = await api.GetProductModelAsync(config.IpAddress);
        result.FirmwareVersion = await api.GetValueAsync(config.IpAddress, HT801ApiClient.PFirmwareVersion);

        // Step 3: Determine expected SIP server IP (our server's IP reachable from the device)
        var expectedSipServer = GetServerIpForDevice(config.IpAddress);

        // Step 4: Validate key settings
        var checks = new (string setting, string pValue, string expected)[]
        {
            ("SIP Server", HT801ApiClient.PSipServer, expectedSipServer),
            ("SIP User ID", HT801ApiClient.PSipUserId, config.Extension),
            ("SIP Port", HT801ApiClient.PSipPort, config.SipServerPort.ToString()),
        };

        var fixes = new Dictionary<string, string>();

        foreach (var (setting, pValue, expected) in checks)
        {
            var actual = await api.GetValueAsync(config.IpAddress, pValue) ?? "";
            // Empty values use device defaults — P40 defaults to 5060
            if (string.IsNullOrEmpty(actual) && pValue == HT801ApiClient.PSipPort)
                actual = "5060";

            var match = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            var item = new HT801ValidationItem
            {
                Setting = setting,
                PValue = pValue,
                Expected = expected,
                Actual = actual,
                Match = match
            };

            if (!match && autoFix)
                fixes[pValue] = expected;

            result.Items.Add(item);
        }

        // Step 5: Apply fixes if requested
        if (autoFix && fixes.Count > 0)
        {
            _logger.LogInformation("Auto-fixing {Count} HT801 settings for {PhoneId}", fixes.Count, phoneId);
            if (await api.SetValuesAsync(config.IpAddress, fixes))
            {
                result.FixedCount = fixes.Count;
                foreach (var item in result.Items)
                {
                    if (!item.Match && fixes.ContainsKey(item.PValue))
                        item.WasFixed = true;
                }
            }
        }

        result.IsValid = result.Items.All(i => i.Match || i.WasFixed);
        return result;
    }

    /// <summary>
    /// Determine our server's IP as seen from the HT801's network.
    /// </summary>
    private string GetServerIpForDevice(string deviceIp)
    {
        // If SipListenAddress is explicit (not 0.0.0.0), use it
        if (_appConfig.SipListenAddress != "0.0.0.0")
            return _appConfig.SipListenAddress;

        // Find the local IP on the same subnet as the device
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse(deviceIp), 1);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine local IP for device {DeviceIp}", deviceIp);
        }

        return "192.168.1.100"; // fallback
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
