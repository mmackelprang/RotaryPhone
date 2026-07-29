using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Diagnostics;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Platform;
using RotaryPhoneController.Server.Hubs;

namespace RotaryPhoneController.Server.Services;

public class SignalRNotifierService : IHostedService
{
    private readonly PhoneManagerService _phoneManager;
    private readonly IHubContext<RotaryHub> _hubContext;
    private readonly ILogger<SignalRNotifierService> _logger;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly ISipAdapter _sipAdapter;
    private readonly AppConfiguration _config;
    private readonly IBluetoothDeviceManager? _deviceManager;
    private readonly SipDiagnosticService _diagnostics;
    private readonly IBellFailureTracker _bellFailureTracker;
    private readonly IHT801ConfigService _ht801Service;
    private bool _lastBluetoothConnected;

    // Cached HT801 reachability. The probe runs on a slow cadence inside the existing 1s monitor
    // loop; BroadcastSystemStatusAsync reads these fields rather than probing synchronously, so a
    // status broadcast is never blocked on a network timeout.
    private static readonly TimeSpan Ht801ProbeInterval = TimeSpan.FromSeconds(30);
    private bool? _ht801Reachable;
    private DateTime? _ht801LastCheckedUtc;
    private string? _ht801ProbedAddress;
    private DateTime _ht801NextProbeUtc = DateTime.MinValue;

    public SignalRNotifierService(
        PhoneManagerService phoneManager,
        IHubContext<RotaryHub> hubContext,
        ILogger<SignalRNotifierService> logger,
        IBluetoothHfpAdapter bluetoothAdapter,
        ISipAdapter sipAdapter,
        AppConfiguration config,
        SipDiagnosticService diagnostics,
        IBellFailureTracker bellFailureTracker,
        IHT801ConfigService ht801Service,
        IBluetoothDeviceManager? deviceManager = null)
    {
        _phoneManager = phoneManager;
        _hubContext = hubContext;
        _logger = logger;
        _bluetoothAdapter = bluetoothAdapter;
        _sipAdapter = sipAdapter;
        _config = config;
        _diagnostics = diagnostics;
        _bellFailureTracker = bellFailureTracker;
        _ht801Service = ht801Service;
        _deviceManager = deviceManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SignalR Notifier Service");

        // Subscribe to phone state changes
        foreach (var (phoneId, manager) in _phoneManager.GetAllPhones())
        {
            _logger.LogInformation("Subscribing to events for phone: {PhoneId}", phoneId);
            manager.StateChanged += () => OnStateChanged(phoneId, manager);
        }

        // Subscribe to IBluetoothDeviceManager events (multi-device)
        if (_deviceManager != null)
        {
            _deviceManager.OnDeviceConnected += dev =>
                _hubContext.Clients.All.SendAsync("DeviceConnected", dev.Address, dev.Name);
            _deviceManager.OnDeviceDisconnected += dev =>
                _hubContext.Clients.All.SendAsync("DeviceDisconnected", dev.Address);
            _deviceManager.OnDeviceDiscovered += dev =>
                _hubContext.Clients.All.SendAsync("DeviceDiscovered", dev.Address, dev.Name);
            _deviceManager.OnDevicePaired += dev =>
                _hubContext.Clients.All.SendAsync("DevicePaired", dev.Address, dev.Name);
            _deviceManager.OnDeviceRemoved += dev =>
                _hubContext.Clients.All.SendAsync("DeviceRemoved", dev.Address);
            _deviceManager.OnPairingRequest += req =>
                _hubContext.Clients.All.SendAsync("PairingRequest", req.Address, req.Type, req.Passkey);
        }

        // Subscribe to SIP diagnostic events for real-time broadcasting
        _diagnostics.OnSipMessageLogged += entry =>
            _hubContext.Clients.All.SendAsync("SipMessage", entry);
        _diagnostics.OnDiagnosisGenerated += (issue, suggestions) =>
            _hubContext.Clients.All.SendAsync("SipDiagnosis", issue, suggestions);
        _diagnostics.OnHt801HealthUpdate += status =>
            _hubContext.Clients.All.SendAsync("Ht801Health", status);
        _diagnostics.OnCallTimelineEvent += entry =>
            _hubContext.Clients.All.SendAsync("CallTimeline", entry);

        // --- Bell failure surfacing ------------------------------------------------------------
        // The socket-level failure path (CallManager) records straight into the tracker. This pair
        // of subscriptions covers the delayed evidence: the INVITE reached the wire but the HT801
        // never answered it (timeout) or refused it (4xx). That is the failure that actually fires
        // in production — a UDP send to a dead-but-routable address succeeds at the socket level.
        _diagnostics.OnSentInviteFailed += (callId, reason, target, detail) =>
        {
            var resolved = ResolveTargetPhone();
            if (resolved == null) return;

            var (phoneId, manager) = resolved.Value;
            _bellFailureTracker.RecordFailure(phoneId, reason, manager.IncomingPhoneNumber,
                manager.CallId, target, detail, DateTime.UtcNow);
        };

        _diagnostics.OnSentInviteSucceeded += callId =>
        {
            var resolved = ResolveTargetPhone();
            resolved?.Manager.NotifyBellRingSucceeded();
        };

        // The ONLY emission point for the hub event. Both detection paths converge on the tracker
        // first, so exactly one BellInviteFailed goes out per recorded failure.
        _bellFailureTracker.OnBellFailure += (phoneId, record) =>
            _hubContext.Clients.All.SendAsync("BellInviteFailed", new
            {
                phoneId,
                callId = record.CallId,
                direction = "Inbound",
                callerNumber = record.CallerNumber,
                occurredAtUtc = record.OccurredAtUtc,
                reason = record.Reason.ToString(),
                target = record.Target,
                detail = record.Detail
            });

        _bellFailureTracker.OnBellRecovered += phoneId =>
            _hubContext.Clients.All.SendAsync("BellRecovered", new
            {
                phoneId,
                occurredAtUtc = DateTime.UtcNow
            });

        // Track initial Bluetooth state
        _lastBluetoothConnected = _bluetoothAdapter.IsConnected;

        // Start monitoring Bluetooth connection changes
        _ = MonitorBluetoothConnectionAsync(cancellationToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Works out which phone an INVITE-outcome signal belongs to.
    ///
    /// Correlation rule: prefer the phone currently in <see cref="CallState.Ringing"/> — a sent
    /// INVITE is a bell ring, and only a ringing phone has one outstanding. If none is Ringing, fall
    /// back to the single registered phone (the overwhelmingly common single-ATA deployment). With
    /// several phones and none ringing there is no honest answer, so the event is DROPPED rather
    /// than pinned on an arbitrary phone — a misattributed bell alert is worse than a missing one.
    /// </summary>
    private (string PhoneId, CallManager Manager)? ResolveTargetPhone()
    {
        var phones = _phoneManager.GetAllPhones().ToList();

        var ringing = phones.FirstOrDefault(p => p.CallManager.CurrentState == CallState.Ringing);
        if (ringing.CallManager != null)
        {
            return (ringing.PhoneId, ringing.CallManager);
        }

        if (phones.Count == 1)
        {
            return (phones[0].PhoneId, phones[0].CallManager);
        }

        _logger.LogDebug(
            "INVITE outcome could not be attributed to a phone ({Count} phones, none ringing) — dropping",
            phones.Count);
        return null;
    }

    private void OnStateChanged(string phoneId, CallManager manager)
    {
        _logger.LogInformation("Broadcasting state change for {PhoneId}: {State}", phoneId, manager.CurrentState);
        _hubContext.Clients.All.SendAsync("CallStateChanged", phoneId, manager.CurrentState.ToString());

        // Also broadcast IncomingCall with the phone number when ringing
        if (manager.CurrentState == CallState.Ringing && manager.IncomingPhoneNumber != null)
        {
            _logger.LogInformation("Broadcasting IncomingCall for {PhoneId}: {Number}", phoneId, manager.IncomingPhoneNumber);
            _hubContext.Clients.All.SendAsync("IncomingCall", phoneId, manager.IncomingPhoneNumber);
        }
    }

    private async Task MonitorBluetoothConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var currentConnected = _bluetoothAdapter.IsConnected;

                // If connection state changed, broadcast system status
                if (currentConnected != _lastBluetoothConnected)
                {
                    _lastBluetoothConnected = currentConnected;
                    _logger.LogInformation("Bluetooth connection changed: {Connected}", currentConnected);
                    await BroadcastSystemStatusAsync();
                }

                await ProbeHt801IfDueAsync();

                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error monitoring Bluetooth connection");
            }
        }
    }

    /// <summary>
    /// Probes HT801 reachability on a slow cadence from inside the 1-second monitor loop, and
    /// broadcasts system status whenever the reachable value CHANGES — including the first
    /// null -> bool transition, which is what turns "Unknown" into a real answer in the UI.
    /// </summary>
    private async Task ProbeHt801IfDueAsync()
    {
        if (DateTime.UtcNow < _ht801NextProbeUtc) return;
        _ht801NextProbeUtc = DateTime.UtcNow + Ht801ProbeInterval;

        var phoneId = _config.Phones.FirstOrDefault()?.Id ?? "default";
        var address = _ht801Service.GetConfig(phoneId).IpAddress;

        // No usable address is a CONFIGURATION problem, not an offline device. Leave the reachable
        // value null ("Unknown") rather than reporting a device we never asked about as down.
        if (string.IsNullOrWhiteSpace(address) || address == "0.0.0.0")
        {
            return;
        }

        bool? reachable;
        try
        {
            var probe = await _ht801Service.TestConnectionAsync(address);
            reachable = probe.Success;
        }
        catch (Exception ex)
        {
            // A probe that threw tells us nothing about the device — stay Unknown.
            _logger.LogDebug(ex, "HT801 reachability probe failed for {Address}", address);
            reachable = null;
        }

        var changed = reachable != _ht801Reachable || address != _ht801ProbedAddress;

        _ht801Reachable = reachable;
        _ht801LastCheckedUtc = DateTime.UtcNow;
        _ht801ProbedAddress = address;

        if (changed)
        {
            _logger.LogInformation("HT801 reachability changed: {Address} -> {Reachable}",
                address, reachable?.ToString() ?? "Unknown");
            await BroadcastSystemStatusAsync();
        }
    }

    private async Task BroadcastSystemStatusAsync()
    {
        var status = new SystemStatus
        {
            Platform = PlatformDetector.CurrentPlatform.ToString(),
            IsRaspberryPi = PlatformDetector.IsRaspberryPi,
            BluetoothEnabled = _config.UseActualBluetoothHfp,
            BluetoothConnected = _bluetoothAdapter.IsConnected,
            BluetoothDeviceAddress = _bluetoothAdapter.ConnectedDeviceAddress,
            SipListening = _sipAdapter.IsListening,
            SipListenAddress = _config.SipListenAddress,
            SipPort = _config.SipPort,
            // Read the cached probe result — never probe synchronously here, or every status
            // broadcast would block on a network timeout.
            Ht801IpAddress = _ht801ProbedAddress,
            Ht801Reachable = _ht801Reachable,
            Ht801LastCheckedUtc = _ht801LastCheckedUtc
        };

        _logger.LogDebug("Broadcasting system status: Bluetooth={Connected}, SIP={Listening}",
            status.BluetoothConnected, status.SipListening);

        await _hubContext.Clients.All.SendAsync("SystemStatusChanged", status);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
