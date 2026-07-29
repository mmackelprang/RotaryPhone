using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Diagnostics;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Platform;
using RotaryPhoneController.Core.Sip;
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
    private readonly IRegistrarBindingStore _bindingStore;
    private bool _lastBluetoothConnected;

    // Cached HT801 reachability. The probe is kicked off on a slow cadence from the existing 1s
    // monitor loop but runs OFF it, so neither the loop nor a status broadcast is ever blocked on a
    // network timeout — BroadcastSystemStatusAsync just reads these fields.
    private static readonly TimeSpan Ht801ProbeInterval = TimeSpan.FromSeconds(30);
    private bool? _ht801Reachable;
    private DateTime? _ht801LastCheckedUtc;
    private string? _ht801ProbedAddress;
    private DateTime _ht801NextProbeUtc = DateTime.MinValue;

    // 0 = no probe running, 1 = one in flight. Claimed with Interlocked so the fire-and-forget
    // probe started by the monitor loop can never overlap with itself.
    private int _probeInFlight;

    // Where each in-flight INVITE came from, keyed by SIP Call-ID.
    //
    // The SIP Call-ID is the ONLY stable link between an INVITE and its outcome: the outcome signal
    // (timeout / 4xx) lands up to 5 seconds after the send, and reading the phone + call id live at
    // that point attributes the failure to whatever is ringing THEN. A hang-up-and-redial inside that
    // window would pin the old call's failure on the new call — exactly the mis-attribution
    // CallManager.CallId exists to prevent. So the origin is captured at SEND time and looked up by
    // Call-ID when the outcome arrives.
    private readonly ConcurrentDictionary<string, (string PhoneId, string? CallId)> _inviteOrigins = new();

    // Entries are normally removed within 5s (success, 4xx, or timeout). This appliance drives a
    // single ATA, so anything approaching this many live INVITEs means entries are leaking rather
    // than accumulating legitimately — drop the lot instead of growing without bound.
    private const int MaxInviteOrigins = 64;

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
        IRegistrarBindingStore bindingStore,
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
        _bindingStore = bindingStore;
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
        _diagnostics.OnSipMessageLogged += RememberInviteOrigin;
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
            // Prefer the origin captured when the INVITE was SENT — see _inviteOrigins. A missing
            // snapshot must never silence a real failure, so fall back to resolving live.
            if (_inviteOrigins.TryRemove(callId, out var origin))
            {
                var callerNumber = _phoneManager.GetPhone(origin.PhoneId)?.IncomingPhoneNumber;
                _bellFailureTracker.RecordFailure(origin.PhoneId, reason, callerNumber,
                    origin.CallId, target, detail, DateTime.UtcNow);
                return;
            }

            var resolved = ResolveTargetPhone();
            if (resolved == null) return;

            var (phoneId, manager) = resolved.Value;
            _bellFailureTracker.RecordFailure(phoneId, reason, manager.IncomingPhoneNumber,
                manager.CallId, target, detail, DateTime.UtcNow);
        };

        _diagnostics.OnSentInviteSucceeded += callId =>
        {
            if (_inviteOrigins.TryRemove(callId, out var origin)
                && _phoneManager.GetPhone(origin.PhoneId) is { } originManager)
            {
                originManager.NotifyBellRingSucceeded();
                return;
            }

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
    /// Snapshots which phone (and which of its calls) a sent INVITE belongs to, keyed by SIP Call-ID.
    /// Runs at SEND time precisely because the outcome arrives seconds later — see _inviteOrigins.
    ///
    /// A DiagnosticNote means the send already failed at the socket level; CallManager reported that
    /// synchronously and SipDiagnosticService does not track it, so there is no outcome to correlate.
    /// </summary>
    private void RememberInviteOrigin(SipMessageEntry entry)
    {
        if (!string.Equals(entry.Method, "INVITE", StringComparison.OrdinalIgnoreCase)
            || entry.Direction != SipDirection.Sent
            || entry.CallId is null
            || entry.DiagnosticNote is not null)
        {
            return;
        }

        // No attributable phone means no useful snapshot — the failure handler's live fallback
        // reaches the same conclusion, so store nothing rather than a bogus origin.
        if (ResolveTargetPhone() is not { } resolved) return;

        // Bounded growth guard: entries are normally removed within 5s, so a full table means they
        // are leaking. Clearing costs at most a fallback to live resolution on the pending outcomes.
        if (_inviteOrigins.Count > MaxInviteOrigins)
        {
            _logger.LogWarning(
                "INVITE origin table exceeded {Max} entries — clearing. Outcomes still in flight will " +
                "fall back to live phone resolution.", MaxInviteOrigins);
            _inviteOrigins.Clear();
        }

        _inviteOrigins[entry.CallId] = (resolved.PhoneId, resolved.Manager.CallId);
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

                StartHt801ProbeIfDue();

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
    /// Kicks off the reachability probe when it is due, WITHOUT blocking the 1-second monitor loop.
    /// TestConnectionAsync waits up to 3s for a ping reply; awaiting it inline delayed Bluetooth
    /// connection-change detection by up to 3s once every 30s. Interlocked claims the slot so probes
    /// can never overlap, and the task swallows everything — it must never fault unobserved.
    /// </summary>
    private void StartHt801ProbeIfDue()
    {
        if (DateTime.UtcNow < _ht801NextProbeUtc) return;
        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0) return;

        _ht801NextProbeUtc = DateTime.UtcNow + Ht801ProbeInterval;

        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeHt801Async();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HT801 reachability probe failed");
            }
            finally
            {
                Volatile.Write(ref _probeInFlight, 0);
            }
        });
    }

    /// <summary>
    /// Probes HT801 reachability and broadcasts system status whenever the reachable value CHANGES —
    /// including the first null -> bool transition, which is what turns "Unknown" into a real answer
    /// in the UI.
    /// </summary>
    private async Task ProbeHt801Async()
    {
        // Probe the address an INVITE would ACTUALLY be sent to, resolved the same way
        // SIPSorceryAdapter.ResolveTargetAddress resolves it. IHT801ConfigService.GetConfig is a
        // last-wins projection — it seeds from AppConfiguration then overwrites from
        // data/ht801-config.json — so a stale on-disk file would have us report a fully working
        // device as unreachable, which is precisely the class of untruth this work removes.
        var address = _bindingStore.GetSingle() is { } b && b.IsFresh(DateTime.UtcNow)
            ? b.Address
            : _config.Phones.FirstOrDefault()?.HT801IpAddress;

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
