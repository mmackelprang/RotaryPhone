using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core;

/// <summary>
/// Service to manage multiple rotary phone instances
/// </summary>
public class PhoneManagerService
{
    private readonly ILogger<PhoneManagerService> _logger;
    // Case-insensitive to match the duplicate-Id guards in InitializePhones and
    // AppConfigurationValidator. With an ordinal comparer here, "default" and "Default" would be
    // rejected by those guards but would still have been distinct keys in this dictionary — an
    // inconsistency that would silently defeat the fail-loud intent if RegisterPhone were ever
    // called directly. Also makes GetPhone tolerant of casing in API route parameters.
    private readonly Dictionary<string, CallManager> _phoneManagers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ICallHistoryService? _callHistoryService;
    private readonly AppConfiguration _config;
    private readonly ISipAdapter _sipAdapter;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly IRtpAudioBridge _rtpBridge;
    private readonly ILogger<CallManager> _callManagerLogger;
    private readonly IBluetoothDeviceManager? _deviceManager;
    private readonly ICallAdapterRegistry? _adapterRegistry;
    private readonly IBellFailureTracker? _bellFailureTracker;

    public PhoneManagerService(
        ILogger<PhoneManagerService> logger,
        AppConfiguration config,
        ISipAdapter sipAdapter,
        IBluetoothHfpAdapter bluetoothAdapter,
        IRtpAudioBridge rtpBridge,
        ILogger<CallManager> callManagerLogger,
        ICallHistoryService? callHistoryService = null,
        IBluetoothDeviceManager? deviceManager = null,
        ICallAdapterRegistry? adapterRegistry = null,
        // LAST so existing positional call sites keep compiling.
        IBellFailureTracker? bellFailureTracker = null)
    {
        _logger = logger;
        _config = config;
        _sipAdapter = sipAdapter;
        _bluetoothAdapter = bluetoothAdapter;
        _rtpBridge = rtpBridge;
        _callManagerLogger = callManagerLogger;
        _callHistoryService = callHistoryService;
        _deviceManager = deviceManager;
        _adapterRegistry = adapterRegistry;
        _bellFailureTracker = bellFailureTracker;

        InitializePhones();

        _logger.LogInformation("PhoneManagerService initialized");
    }

    private void InitializePhones()
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var phoneConfig in _config.Phones)
        {
            // Fail loudly rather than discarding. The previous behaviour (warn + return) silently
            // kept the FIRST entry — which the configuration binder had appended a hardcoded
            // default ahead of — and rang a stale address.
            if (!seenIds.Add(phoneConfig.Id))
            {
                throw new InvalidOperationException(
                    $"Duplicate phone Id '{phoneConfig.Id}' in RotaryPhone:Phones. " +
                    "Each configured phone must have a unique Id.");
            }

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
            throw new InvalidOperationException(
                $"Phone '{phoneId}' is already registered. Re-registering would silently discard " +
                "the new configuration — check RotaryPhone:Phones for duplicate Ids.");
        }

        var callManager = new CallManager(
            sipAdapter,
            bluetoothAdapter,
            rtpBridge,
            callManagerLogger,
            phoneConfig,
            rtpPort,
            _callHistoryService,
            _deviceManager,
            _adapterRegistry,
            outboundDialingTimeout: null,
            bellFailureTracker: _bellFailureTracker);

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
