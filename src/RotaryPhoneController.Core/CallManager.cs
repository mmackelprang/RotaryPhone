using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core;

public class CallManager
{
    private readonly ISipAdapter _sipAdapter;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly IRtpAudioBridge _rtpBridge;
    private readonly ICallHistoryService? _callHistoryService;
    private readonly ILogger<CallManager> _logger;
    private readonly RotaryPhoneConfig _phoneConfig;
    private readonly IBluetoothDeviceManager? _deviceManager;
    private readonly ICallAdapterRegistry? _adapterRegistry;
    private ICallAdapter? _boundAdapter;
    private readonly int _rtpPort;
    private CancellationTokenSource? _ringingTimeoutCts;
    private CancellationTokenSource? _outboundDialingTimeoutCts;
    private readonly TimeSpan _outboundDialingTimeout;
    private CallState _currentState;
    private string _dialedNumber = string.Empty;
    private CallHistoryEntry? _currentCallHistory;
    private bool _isHangingUp;
    private string? _activeDeviceAddress;

    // Set to 1 after an outbound GV INVITE is placed; the call stays in Dialing until
    // GV signals the cell answered (CallStatusType.Active -> OnCallAnswered). Distinguishes
    // an outbound-placed Dialing call from an inbound-ringing one in the answer handler.
    //
    // Two threads race to act on the pending outbound call — the GV-answer event (may also
    // fire more than once, e.g. on a re-INVITE 200 OK) and the no-answer timeout continuation
    // (TaskScheduler.Default thread). Both go through TryClaimOutboundConnect(), which uses
    // Interlocked.CompareExchange(1 -> 0) so EXACTLY ONE caller wins. This makes the
    // bridge-start idempotent AND closes the TOCTOU window where a late timeout could
    // HangUp() a call the answer path just bridged.
    private int _outboundConnectPending;

    /// <summary>
    /// Atomically claims the pending outbound connect. Returns true to exactly one caller
    /// (answer event OR no-answer timeout); all subsequent callers get false.
    /// </summary>
    private bool TryClaimOutboundConnect() =>
        Interlocked.CompareExchange(ref _outboundConnectPending, 0, 1) == 1;

    // RTP port details negotiated from HT801's 200 OK SDP response
    private int? _negotiatedRtpPort;
    private string? _negotiatedRtpIp;

    public event Action? StateChanged;

    public CallState CurrentState
    {
        get => _currentState;
        private set
        {
            if (_currentState != value)
            {
                _currentState = value;
                _logger.LogInformation("State changed to: {State}", _currentState);
                StateChanged?.Invoke();
            }
        }
    }

    public string DialedNumber
    {
        get => _dialedNumber;
        private set
        {
            _dialedNumber = value;
            _logger.LogInformation("Dialed number updated: {Number}", _dialedNumber);
        }
    }

    /// <summary>
    /// Phone number of the current incoming call (set during Ringing state, cleared on Idle).
    /// </summary>
    public string? IncomingPhoneNumber { get; private set; }

    /// <summary>
    /// UTC timestamp when the current call entered InCall state.
    /// Added for GVTrunk dashboard duration display. Deviation from GVTrunk spec section 3.3
    /// (approved: property is useful for all callers, not GVTrunk-specific).
    /// </summary>
    public DateTime? CallStartedAtUtc { get; private set; }

    public CallManager(
        ISipAdapter sipAdapter,
        IBluetoothHfpAdapter bluetoothAdapter,
        IRtpAudioBridge rtpBridge,
        ILogger<CallManager> logger,
        RotaryPhoneConfig phoneConfig,
        int rtpPort = 49000,
        ICallHistoryService? callHistoryService = null,
        IBluetoothDeviceManager? deviceManager = null,
        ICallAdapterRegistry? adapterRegistry = null,
        TimeSpan? outboundDialingTimeout = null)
    {
        _sipAdapter = sipAdapter;
        _bluetoothAdapter = bluetoothAdapter;
        _rtpBridge = rtpBridge;
        _logger = logger;
        _phoneConfig = phoneConfig;
        _rtpPort = rtpPort;
        _callHistoryService = callHistoryService;
        _deviceManager = deviceManager;
        _adapterRegistry = adapterRegistry;
        // If an outbound GV call is placed but never reaches Active (cell never answers),
        // reset cleanly to Idle after this timeout so the call doesn't hang in Dialing.
        // Default ~45s; injectable for fast, deterministic unit tests.
        _outboundDialingTimeout = outboundDialingTimeout ?? TimeSpan.FromSeconds(45);
        _currentState = CallState.Idle;
    }

    public void Initialize()
    {
        _logger.LogInformation("Initializing CallManager for phone: {PhoneId} ({PhoneName})", 
            _phoneConfig.Id, _phoneConfig.Name);
        
        // Subscribe to SIP adapter events
        _sipAdapter.OnHookChange += HandleHookChange;
        _sipAdapter.OnDigitsReceived += HandleDigitsReceived;
        _sipAdapter.OnIncomingCall += HandleIncomingCall;
        _sipAdapter.OnRtpDetailsNegotiated += HandleRtpDetailsNegotiated;
        
        // Subscribe to Bluetooth HFP events
        _bluetoothAdapter.OnIncomingCall += HandleBluetoothIncomingCall;
        _bluetoothAdapter.OnCallAnsweredOnCellPhone += HandleCallAnsweredOnCellPhone;
        _bluetoothAdapter.OnCallEnded += HandleBluetoothCallEnded;
        _bluetoothAdapter.OnAudioRouteChanged += HandleAudioRouteChanged;

        // Subscribe to multi-device BT manager events (if available)
        if (_deviceManager != null)
        {
            _deviceManager.OnIncomingCall += HandleDeviceIncomingCall;
            _deviceManager.OnCallAnsweredOnPhone += HandleDeviceCallAnsweredOnPhone;
            _deviceManager.OnCallActive += HandleDeviceCallActive;
            _deviceManager.OnCallEnded += HandleDeviceCallEnded;
            _deviceManager.OnScoAudioConnected += HandleScoConnected;
            _deviceManager.OnScoAudioDisconnected += HandleScoDisconnected;
        }

        // Call adapter registry (multi-mode support)
        if (_adapterRegistry != null)
        {
            _adapterRegistry.OnModeChanged += _ => RebindAdapterEvents();
            RebindAdapterEvents();
        }

        _logger.LogInformation("CallManager initialized successfully");
    }

    private void RebindAdapterEvents()
    {
        if (_adapterRegistry == null) return;

        // Unsubscribe from previous adapter
        if (_boundAdapter != null)
        {
            _boundAdapter.OnIncomingCall -= HandleAdapterIncomingCall;
            _boundAdapter.OnCallAnswered -= HandleAdapterCallAnswered;
            _boundAdapter.OnCallEnded -= HandleAdapterCallEnded;
        }

        try
        {
            _boundAdapter = _adapterRegistry.ActiveAdapter;
        }
        catch (InvalidOperationException)
        {
            _boundAdapter = null;
            _logger.LogWarning("No active call adapter available");
            return;
        }

        // Subscribe to new adapter
        _boundAdapter.OnIncomingCall += HandleAdapterIncomingCall;
        _boundAdapter.OnCallAnswered += HandleAdapterCallAnswered;
        _boundAdapter.OnCallEnded += HandleAdapterCallEnded;

        _logger.LogInformation("CallManager bound to adapter: {Mode}", _boundAdapter.Mode);
    }

    private void HandleAdapterIncomingCall(string phoneNumber)
    {
        HandleBluetoothIncomingCall(phoneNumber);
    }

    private void HandleAdapterCallAnswered()
    {
        HandleCallAnsweredOnCellPhone();
    }

    private void HandleAdapterCallEnded()
    {
        if (CurrentState == CallState.Idle) return;
        HandleBluetoothCallEnded();
    }

    private void HandleRtpDetailsNegotiated(int port, string ip)
    {
        _negotiatedRtpPort = port;
        _negotiatedRtpIp = ip;
        _logger.LogInformation("CallManager captured negotiated RTP from HT801 SDP: {IP}:{Port}", ip, port);
    }

    public void HandleHookChange(bool isOffHook)
    {
        _logger.LogInformation("Hook change: {State}", isOffHook ? "OFF-HOOK" : "ON-HOOK");

        if (isOffHook)
        {
            // Handset lifted
            switch (CurrentState)
            {
                case CallState.Idle:
                    // When a GV adapter is active, BT devices aren't required for dialing.
                    // Only enforce BT device check for Bluetooth/SipTrunk modes.
                    if (_boundAdapter != null &&
                        (_boundAdapter.Mode == CallAdapterMode.GVApi || _boundAdapter.Mode == CallAdapterMode.GVBrowser))
                    {
                        // GV mode — no BT device needed for outbound calls
                        _logger.LogInformation("Off-hook in GV mode ({Mode}) — entering dialing state", _boundAdapter.Mode);
                    }
                    else if (_deviceManager != null)
                    {
                        var connected = _deviceManager.ConnectedDevices;
                        if (connected.Count == 0)
                        {
                            _logger.LogWarning("No BT devices connected — cannot make calls");
                            return;
                        }
                        _activeDeviceAddress = connected[0].Address;
                    }
                    CurrentState = CallState.Dialing;
                    DialedNumber = string.Empty;
                    break;

                case CallState.Ringing:
                    // User answered incoming call
                    AnswerCall();
                    break;
            }
        }
        else
        {
            // Handset placed on hook - always hang up
            HangUp();
        }
    }

    public void HandleDigitsReceived(string number)
    {
        _logger.LogInformation("Digits received: {Number}, current state: {State}", number, CurrentState);

        // Ignore non-numeric "numbers" (e.g., HT801 registration name "rotaryphone")
        if (!number.All(c => char.IsDigit(c) || c == '+' || c == '*' || c == '#'))
        {
            _logger.LogDebug("Ignoring non-numeric dial string: {Number}", number);
            return;
        }

        DialedNumber = number;

        if (CurrentState == CallState.Dialing)
        {
            // Transition to InCall and start the call
            StartCall(number);
        }
        else if (CurrentState == CallState.Idle)
        {
            // HT801 sends INVITE with full dialed number before hook change event.
            // Treat this as an implicit off-hook + dial.
            _logger.LogInformation("Digits received while Idle — implicit off-hook, starting call to {Number}", number);

            // Same setup as HandleHookChange(offHook=true) for Idle state
            if (_boundAdapter != null &&
                (_boundAdapter.Mode == CallAdapterMode.GVApi || _boundAdapter.Mode == CallAdapterMode.GVBrowser))
            {
                // GV mode — no BT device needed
            }
            else if (_deviceManager != null)
            {
                var connected = _deviceManager.ConnectedDevices;
                if (connected.Count == 0)
                {
                    _logger.LogWarning("No BT devices connected — cannot make calls");
                    return;
                }
                _activeDeviceAddress = connected[0].Address;
            }

            CurrentState = CallState.Dialing;
            StartCall(number);
        }
    }

    private void HandleIncomingCall()
    {
        _logger.LogInformation("Incoming call detected");
        
        if (CurrentState == CallState.Idle)
        {
            SimulateIncomingCall();
        }
    }

    public void SimulateIncomingCall()
    {
        _logger.LogInformation("Simulating incoming call");

        if (CurrentState != CallState.Idle)
        {
            _logger.LogWarning("Cannot simulate incoming call - not in Idle state. Current state: {State}", CurrentState);
            return;
        }

        IncomingPhoneNumber = "Unknown";
        CurrentState = CallState.Ringing;
        
        // Send INVITE to HT801 to trigger ring
        _sipAdapter.SendInviteToHT801(_phoneConfig.HT801Extension, _phoneConfig.HT801IpAddress);
        
        // Create call history entry for incoming call
        _currentCallHistory = new CallHistoryEntry
        {
            PhoneNumber = "Unknown",
            Direction = CallDirection.Incoming,
            PhoneId = _phoneConfig.Id,
            StartTime = DateTime.Now
        };
        _callHistoryService?.AddCallHistory(_currentCallHistory);
        
        _logger.LogInformation("Incoming call simulation initiated - HT801 should ring");
    }
    
    private void HandleBluetoothIncomingCall(string phoneNumber)
    {
        _logger.LogInformation("Bluetooth incoming call from: {PhoneNumber}", phoneNumber);

        // If already ringing and we get an updated caller ID (e.g., +CLIP arrived after +CIEV),
        // update the phone number and re-broadcast
        if (CurrentState == CallState.Ringing && phoneNumber != "Unknown" && IncomingPhoneNumber == "Unknown")
        {
            IncomingPhoneNumber = phoneNumber;
            if (_currentCallHistory != null)
                _currentCallHistory.PhoneNumber = phoneNumber;
            _logger.LogInformation("Caller ID updated to: {PhoneNumber}", phoneNumber);
            StateChanged?.Invoke(); // Re-broadcast with updated number
            return;
        }

        if (CurrentState != CallState.Idle)
        {
            _logger.LogWarning("Cannot handle incoming call - not in Idle state. Current state: {State}", CurrentState);
            return;
        }

        IncomingPhoneNumber = phoneNumber;
        CurrentState = CallState.Ringing;

        // Start a ringing timeout — if the call isn't answered or cancelled within 60s,
        // reset to Idle. This prevents the state machine from getting stuck if the
        // SIP INVITE times out or the HT801 doesn't respond.
        _ringingTimeoutCts?.Cancel();
        _ringingTimeoutCts = new CancellationTokenSource();
        var cts = _ringingTimeoutCts;
        _ = Task.Delay(TimeSpan.FromSeconds(60), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && CurrentState == CallState.Ringing)
            {
                _logger.LogWarning("Ringing timeout (60s) — resetting to Idle");
                _sipAdapter.CancelPendingInvite();
                CurrentState = CallState.Idle;
                IncomingPhoneNumber = null;
                _currentCallHistory = null;
                StateChanged?.Invoke();
            }
        }, TaskScheduler.Default);

        // Send INVITE to HT801 to trigger ring.
        // Always use _rtpPort (from config, typically 49000) in the SDP — the audio bridge
        // will bind to this same port so HT801's RTP arrives at the correct destination.
        _logger.LogInformation("CallManager sending INVITE to {Extension}@{IP} (SDP RTP port {Port})",
            _phoneConfig.HT801Extension, _phoneConfig.HT801IpAddress, _rtpPort);
        _sipAdapter.SendInviteToHT801(_phoneConfig.HT801Extension, _phoneConfig.HT801IpAddress, _rtpPort);

        // Create call history entry
        _currentCallHistory = new CallHistoryEntry
        {
            PhoneNumber = phoneNumber,
            Direction = CallDirection.Incoming,
            PhoneId = _phoneConfig.Id,
            StartTime = DateTime.Now
        };
        _callHistoryService?.AddCallHistory(_currentCallHistory);
        
        _logger.LogInformation("Incoming call - ringing rotary phone");
    }

    /// <summary>
    /// Called by Radio.API (via SignalR) when it resolves a caller's name from PBAP contacts.
    /// </summary>
    public void SetResolvedCallerName(string phoneNumber, string displayName)
    {
        if (_currentCallHistory != null && _currentCallHistory.PhoneNumber == phoneNumber)
        {
            _currentCallHistory.CallerName = displayName;
            _logger.LogInformation("Caller resolved: {PhoneNumber} → {DisplayName}", phoneNumber, displayName);
        }
    }

    private void HandleCallAnsweredOnCellPhone()
    {
        _logger.LogInformation("Call answered on cell phone device");

        // OUTBOUND (GV) answered: the cell we dialed picked up. The call has been sitting
        // in Dialing since placement (PlaceGvCallAsync deferred the bridge-start). Start the
        // HT801<->GV audio bridge NOW and transition to InCall. Mirrors the proven BT outbound
        // path (HandleDeviceCallActive). TryClaimOutboundConnect() atomically wins the pending
        // connect exactly once, so a duplicate Active (e.g. re-INVITE 200 OK) — or a racing
        // no-answer timeout — cannot double-start the bridge.
        if (CurrentState == CallState.Dialing && TryClaimOutboundConnect())
        {
            _outboundDialingTimeoutCts?.Cancel();

            _logger.LogInformation(
                "Outbound GV call answered on cell — starting audio bridge " +
                "(HT801 RTP={Ip}:{Port}, localRtpPort={LocalPort})",
                _negotiatedRtpIp ?? "(null)", _negotiatedRtpPort?.ToString() ?? "(null)", _rtpPort);

            // Negotiated RTP was already stashed at placement (PlaceGvCallAsync). Do NOT
            // CancelPendingInvite here — the HT801 leg was already answered with 200 OK and
            // must stay up to carry audio.
            if (_boundAdapter != null)
            {
                _ = _boundAdapter.OnCallAnsweredOnRotaryPhoneAsync();
            }

            CallStartedAtUtc = DateTime.UtcNow;
            CurrentState = CallState.InCall;
            return;
        }

        if (CurrentState != CallState.Ringing)
        {
            _logger.LogWarning("Call answered on cell phone but not in Ringing state. Current state: {State}", CurrentState);
            return;
        }

        // Cancel the SIP INVITE so the rotary phone stops ringing
        _sipAdapter.CancelPendingInvite();

        // Audio stays on the cell phone — no RTP bridge needed
        _logger.LogInformation("Call answered on cell phone — audio stays on phone, rotary ring cancelled");

        // Update call history
        if (_currentCallHistory != null)
        {
            _currentCallHistory.AnsweredOn = CallAnsweredOn.CellPhone;
            _callHistoryService?.UpdateCallHistory(_currentCallHistory);
        }

        CallStartedAtUtc = DateTime.UtcNow;
        CurrentState = CallState.InCall;
    }
    
    private void HandleBluetoothCallEnded()
    {
        if (CurrentState == CallState.Idle) return;
        _logger.LogInformation("Bluetooth call ended");
        HangUp();
    }
    
    private void HandleAudioRouteChanged(AudioRoute route)
    {
        _logger.LogInformation("Audio route changed to: {Route}", route);

        // Update RTP bridge routing if active
        if (_rtpBridge.IsActive && CurrentState == CallState.InCall)
        {
            _ = _rtpBridge.ChangeAudioRouteAsync(route);
        }
    }

    #region Device-Aware Call Handlers (IBluetoothDeviceManager)

    private void HandleDeviceIncomingCall(BluetoothDevice device, string number)
    {
        _activeDeviceAddress = device.Address;
        HandleBluetoothIncomingCall(number);
    }

    private void HandleDeviceCallAnsweredOnPhone(BluetoothDevice device)
    {
        if (_activeDeviceAddress == device.Address)
            HandleCallAnsweredOnCellPhone();
    }

    private void HandleDeviceCallActive(BluetoothDevice device)
    {
        if (_activeDeviceAddress == device.Address && CurrentState == CallState.Dialing)
        {
            // Outgoing call connected — transition to InCall
            CallStartedAtUtc = DateTime.UtcNow;
            CurrentState = CallState.InCall;
            _logger.LogInformation("Outgoing call connected on {Device}", device.Address);
        }
    }

    private void HandleDeviceCallEnded(BluetoothDevice device)
    {
        if (_activeDeviceAddress == device.Address)
        {
            HandleBluetoothCallEnded();
            _activeDeviceAddress = null;
        }
    }

    private void HandleScoConnected(BluetoothDevice device)
    {
        if (_activeDeviceAddress != device.Address) return;

        _logger.LogInformation("SCO audio connected for {Device} — starting RTP bridge", device.Address);
        var rtpEndpoint = $"{_phoneConfig.HT801IpAddress}:{_rtpPort}";
        _ = _rtpBridge.StartBridgeAsync(rtpEndpoint, AudioRoute.RotaryPhone);
    }

    private void HandleScoDisconnected(BluetoothDevice device)
    {
        if (_activeDeviceAddress != device.Address) return;

        _logger.LogInformation("SCO audio disconnected for {Device} — stopping RTP bridge", device.Address);
        if (_rtpBridge.IsActive)
        {
            _ = _rtpBridge.StopBridgeAsync();
        }
    }

    #endregion

    public void AnswerCall()
    {
        _logger.LogInformation("Answering call");

        if (CurrentState != CallState.Ringing)
        {
            _logger.LogWarning("Cannot answer call - not in Ringing state. Current state: {State}", CurrentState);
            return;
        }

        // Cancel the ringing timeout
        _ringingTimeoutCts?.Cancel();

        // Call answered on rotary phone (handset lifted) - route audio
        _logger.LogInformation("Call answered on rotary phone");

        // Notify the active adapter:
        // - In GV modes (GVBrowser/GVApi): starts audio bridge AND answers the call
        // - In other modes: no-op (default interface implementation)
        if (_boundAdapter != null)
        {
            // Pass negotiated RTP details so the audio bridge binds to the correct ports.
            // _rtpPort is what we advertised in the INVITE SDP (the port HT801 will send TO).
            _boundAdapter.SetNegotiatedRtpDetails(_negotiatedRtpPort, _negotiatedRtpIp, _rtpPort);
            _logger.LogInformation(
                "Calling adapter.OnCallAnsweredOnRotaryPhoneAsync for mode {Mode} " +
                "(negotiated HT801 RTP={NegIp}:{NegPort}, invitePort={InvitePort})",
                _boundAdapter.Mode, _negotiatedRtpIp ?? "(null)",
                _negotiatedRtpPort?.ToString() ?? "(null)", _rtpPort);
            _ = _boundAdapter.OnCallAnsweredOnRotaryPhoneAsync();
        }

        // Start audio based on active adapter mode.
        // In GV modes (GVBrowser/GVApi), audio is handled by GVAudioBridgeService (started above).
        // Only start Bluetooth/SCO or RTP audio for non-GV modes (or when no adapter is bound).
        if (_boundAdapter == null || _boundAdapter.Mode == CallAdapterMode.BluetoothHfp || _boundAdapter.Mode == CallAdapterMode.SipTrunk)
        {
            if (_deviceManager != null && _activeDeviceAddress != null)
            {
                // Bluetooth mode: answer via device manager — sends ATA over RFCOMM, SCO opens,
                // HandleScoConnected will start the RTP bridge
                _ = _deviceManager.AnswerCallAsync(_activeDeviceAddress);
            }
            else
            {
                // Legacy Bluetooth path
                _ = _bluetoothAdapter.AnswerCallAsync(AudioRoute.RotaryPhone);
                var rtpEndpoint = $"{_phoneConfig.HT801IpAddress}:{_rtpPort}";
                _ = _rtpBridge.StartBridgeAsync(rtpEndpoint, AudioRoute.RotaryPhone);
            }
        }

        // Update call history
        if (_currentCallHistory != null)
        {
            _currentCallHistory.AnsweredOn = CallAnsweredOn.RotaryPhone;
            _callHistoryService?.UpdateCallHistory(_currentCallHistory);
        }

        CallStartedAtUtc = DateTime.UtcNow;
        CurrentState = CallState.InCall;
        _logger.LogInformation("Call answered on rotary phone");
    }

    public void StartCall(string number)
    {
        _logger.LogInformation("Starting call to: {Number}", number);

        if (CurrentState != CallState.Dialing)
        {
            _logger.LogWarning("Cannot start call - not in Dialing state. Current state: {State}", CurrentState);
            return;
        }

        // Create call history entry
        _currentCallHistory = new CallHistoryEntry
        {
            PhoneNumber = number,
            Direction = CallDirection.Outgoing,
            PhoneId = _phoneConfig.Id,
            StartTime = DateTime.Now
        };
        _callHistoryService?.AddCallHistory(_currentCallHistory);

        DialedNumber = number;

        // Route based on active call adapter mode
        if (_boundAdapter != null &&
            (_boundAdapter.Mode == CallAdapterMode.GVApi || _boundAdapter.Mode == CallAdapterMode.GVBrowser))
        {
            // Place call through Google Voice adapter
            _logger.LogInformation("Placing outbound call to {Number} via {Mode}", number, _boundAdapter.Mode);
            _ = PlaceGvCallAsync(number);
        }
        else if (_deviceManager != null && _activeDeviceAddress != null)
        {
            // Use device manager — dial via the active BT device, stay in Dialing
            // until call_active event arrives (HandleDeviceCallActive transitions to InCall)
            _logger.LogInformation("Dialing {Number} via device {Device}", number, _activeDeviceAddress);
            _ = _deviceManager.DialAsync(_activeDeviceAddress, number);
        }
        else
        {
            // Legacy path — use old single-device adapter
            _logger.LogInformation("Initiating call via Bluetooth HFP for: {Number}", number);
            _ = _bluetoothAdapter.InitiateCallAsync(number);
            CurrentState = CallState.InCall;
        }

        _logger.LogInformation("Call initiated for {Number}", number);
    }

    /// <summary>
    /// Places an outbound call through the Google Voice adapter.
    /// After PlaceCallAsync returns a call ID, the negotiated RTP details are stashed but the
    /// call stays in Dialing — the audio bridge start and the InCall transition are DEFERRED
    /// until GV signals the cell actually answered (CallStatusType.Active -> OnCallAnswered ->
    /// HandleCallAnsweredOnCellPhone). Starting the bridge at placement (before the far end is
    /// up) streamed early audio and produced a one-shot errno-101 cold-send blip. Deferring it
    /// mirrors the proven BT outbound path (HandleDeviceCallActive: Dialing -> InCall on
    /// call_active). A no-answer timeout resets to Idle if Active never arrives.
    /// </summary>
    private async Task PlaceGvCallAsync(string number)
    {
        try
        {
            var callId = await _boundAdapter!.PlaceCallAsync(number);
            _logger.LogInformation("GV outbound call placed, CallId={CallId}, Number={Number}", callId, number);

            // The HT801's outbound INVITE was already answered by SIPSorceryAdapter with
            // an SDP containing _rtpPort (49000). HT801 is sending RTP to that port.
            // Stash the negotiated RTP details NOW so the audio bridge can bind correctly
            // when it actually starts (on GV answer). The details are captured here but only
            // CONSUMED at answer time.
            _boundAdapter.SetNegotiatedRtpDetails(_negotiatedRtpPort, _negotiatedRtpIp, _rtpPort);

            // Stay in Dialing. The bridge-start + InCall now happen in
            // HandleCallAnsweredOnCellPhone when GV reports the cell answered (Active).
            // Arm the pending flag BEFORE starting the timeout so the timeout's claim is valid.
            Volatile.Write(ref _outboundConnectPending, 1);
            StartOutboundDialingTimeout(number);
            _logger.LogInformation(
                "Outbound GV call placed — staying Dialing until cell answers " +
                "(negotiated HT801 RTP={Ip}:{Port}, localRtpPort={LocalPort})",
                _negotiatedRtpIp ?? "(null)", _negotiatedRtpPort?.ToString() ?? "(null)", _rtpPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place GV outbound call to {Number}", number);
            Volatile.Write(ref _outboundConnectPending, 0);
            _outboundDialingTimeoutCts?.Cancel();
            CurrentState = CallState.Idle;
        }
    }

    /// <summary>
    /// Starts (or restarts) the outbound no-answer timeout. If an outbound GV call placed in
    /// Dialing never reaches Active within _outboundDialingTimeout, hang up cleanly so the
    /// state machine doesn't sit in Dialing forever. Cancelled on answer and on hangup.
    /// </summary>
    private void StartOutboundDialingTimeout(string number)
    {
        _outboundDialingTimeoutCts?.Cancel();
        _outboundDialingTimeoutCts = new CancellationTokenSource();
        var cts = _outboundDialingTimeoutCts;
        _ = Task.Delay(_outboundDialingTimeout, cts.Token).ContinueWith(t =>
        {
            // TryClaimOutboundConnect() atomically wins the pending connect. If the answer
            // event already claimed it (the call was just bridged), this returns false and we
            // do NOT tear down a live call — closing the timeout-vs-answer TOCTOU window.
            if (!t.IsCanceled && CurrentState == CallState.Dialing && TryClaimOutboundConnect())
            {
                _logger.LogWarning(
                    "Outbound dialing timeout ({Seconds:N0}s) — cell never answered {Number}, resetting to Idle",
                    _outboundDialingTimeout.TotalSeconds, number);
                HangUp();
            }
        }, TaskScheduler.Default);
    }

    public void HangUp()
    {
        if (_isHangingUp)
        {
            _logger.LogDebug("HangUp re-entry detected; ignoring to prevent recursion");
            return;
        }

        _isHangingUp = true;
        _ringingTimeoutCts?.Cancel();
        _outboundDialingTimeoutCts?.Cancel();
        _logger.LogInformation("Hanging up");
        var previousState = CurrentState;

        try
        {
            // Cancel any pending SIP INVITE so the HT801 stops ringing
            _sipAdapter.CancelPendingInvite();

            // Terminate Bluetooth call
            if (_deviceManager != null && _activeDeviceAddress != null)
            {
                _ = _deviceManager.HangupCallAsync(_activeDeviceAddress);
            }
            else
            {
                _ = _bluetoothAdapter.TerminateCallAsync();
            }

            // Stop audio bridges
            if (_rtpBridge.IsActive)
            {
                _ = _rtpBridge.StopBridgeAsync();
            }
            if (_boundAdapter != null)
            {
                _ = _boundAdapter.OnCallHungUpAsync();
            }

            // Update call history
            if (_currentCallHistory != null)
            {
                _currentCallHistory.EndTime = DateTime.Now;
                _callHistoryService?.UpdateCallHistory(_currentCallHistory);
                _currentCallHistory = null;
            }

            CurrentState = CallState.Idle;
            DialedNumber = string.Empty;
            IncomingPhoneNumber = null;
            CallStartedAtUtc = null;
            _activeDeviceAddress = null;
            _negotiatedRtpPort = null;
            _negotiatedRtpIp = null;
            Volatile.Write(ref _outboundConnectPending, 0);
            _logger.LogInformation("Call terminated. State reset. Previous state was: {PreviousState}", previousState);
        }
        finally
        {
            _isHangingUp = false;
        }
    }
}
