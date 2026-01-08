using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core;

/// <summary>
/// Service to manage multiple rotary phone instances
/// </summary>
public class PhoneManagerService
{
    private readonly ILogger<PhoneManagerService> _logger;
    private readonly Dictionary<string, CallManager> _phoneManagers = new();
    private readonly ICallHistoryService? _callHistoryService;
    private readonly AppConfiguration _config;
    private readonly ISipAdapter _sipAdapter;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly IRtpAudioBridge _rtpBridge;
    private readonly ILogger<CallManager> _callManagerLogger;

    public PhoneManagerService(
        ILogger<PhoneManagerService> logger, 
        AppConfiguration config,
        ISipAdapter sipAdapter,
        IBluetoothHfpAdapter bluetoothAdapter,
        IRtpAudioBridge rtpBridge,
        ILogger<CallManager> callManagerLogger,
        ICallHistoryService? callHistoryService = null)
    {
        _logger = logger;
        _config = config;
        _sipAdapter = sipAdapter;
        _bluetoothAdapter = bluetoothAdapter;
        _rtpBridge = rtpBridge;
        _callManagerLogger = callManagerLogger;
        _callHistoryService = callHistoryService;
        
        InitializePhones();
        
        _logger.LogInformation("PhoneManagerService initialized");
    }

    private void InitializePhones()
    {
        foreach (var phoneConfig in _config.Phones)
        {
            RegisterPhone(
                phoneConfig.Id,
                _sipAdapter,
                _bluetoothAdapter,
                _rtpBridge,
                _callManagerLogger,
                phoneConfig,
                _config.RtpBasePort);
        }
    }

    /// <summary>
    /// Register a phone instance
    /// </summary>
    public void RegisterPhone(
        string phoneId,
        ISipAdapter sipAdapter,
        IBluetoothHfpAdapter bluetoothAdapter,
        IRtpAudioBridge rtpBridge,
        ILogger<CallManager> callManagerLogger,
        RotaryPhoneConfig phoneConfig,
        int rtpPort)
    {
        if (_phoneManagers.ContainsKey(phoneId))
        {
            _logger.LogWarning("Phone {PhoneId} is already registered", phoneId);
            return;
        }

        var callManager = new CallManager(
            sipAdapter,
            bluetoothAdapter,
            rtpBridge,
            callManagerLogger,
            phoneConfig,
            rtpPort,
            _callHistoryService);
        
        callManager.Initialize();
        _phoneManagers[phoneId] = callManager;
        
        _logger.LogInformation("Registered phone: {PhoneId} ({PhoneName})", phoneId, phoneConfig.Name);
    }

    /// <summary>
    /// Get a phone manager by ID
    /// </summary>
    public CallManager? GetPhone(string phoneId)
    {
        return _phoneManagers.GetValueOrDefault(phoneId);
    }

    /// <summary>
    /// Get all registered phones
    /// </summary>
    public IEnumerable<(string PhoneId, CallManager CallManager)> GetAllPhones()
    {
        return _phoneManagers.Select(kvp => (kvp.Key, kvp.Value));
    }

    /// <summary>
    /// Get count of registered phones
    /// </summary>
    public int PhoneCount => _phoneManagers.Count;
}
