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

    public PhoneManagerService(ILogger<PhoneManagerService> logger, ICallHistoryService? callHistoryService = null)
    {
        _logger = logger;
        _callHistoryService = callHistoryService;
        _logger.LogInformation("PhoneManagerService initialized");
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
