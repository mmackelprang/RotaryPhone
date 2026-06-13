using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Sip;

namespace RotaryPhoneController.GVBridge.Adapters;

/// <summary>
/// ICallAdapter implementation that uses SIP-over-WebSocket transport
/// (RFC 7118) to Google Voice for call signaling and DTLS-SRTP for audio.
/// Cookie-authenticated HTTP API is used only for health checks and SIP credential retrieval.
/// </summary>
public class GVApiAdapter : ICallAdapter, IDisposable
{
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GVApiAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Set via SetAudioBridge() to avoid circular DI
    private GVAudioBridgeService? _audioBridge;

    // Internal components created during ActivateAsync
    private GvCookieStore? _cookieStore;
    private GvCookieSet? _cookieSet;
    private HttpClient? _httpClient;
    private GvAccountClient? _accountClient;
    private GvSipTransport? _sipTransport;
    private Timer? _healthCheckTimer;

    private string? _activeCallId;
    private bool _disposed;
    private bool _areCookiesValid;

    // When the rotating freshness cookies (PSIDTS) were last loaded/refreshed (UTC).
    private DateTime? _psidtsRefreshedAt;

    // Negotiated RTP details from HT801's SDP 200 OK response (set by CallManager)
    private int? _negotiatedHt801RtpPort;
    private string? _negotiatedHt801RtpIp;
    private int? _inviteRtpPort;  // the port we offered in the INVITE SDP

    public CallAdapterMode Mode => CallAdapterMode.GVApi;
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Whether the SIP transport is currently registered with Google Voice.
    /// Honest: backed by IsRegistered, which is now (_registered AND socket connected).
    /// </summary>
    public bool IsSipRegistered => _sipTransport?.IsRegistered ?? false;

    /// <summary>
    /// Whether the underlying SIP WebSocket is currently connected (independent of registration).
    /// </summary>
    public bool IsWebSocketConnected => _sipTransport?.IsConnected ?? false;

    /// <summary>
    /// UTC timestamp of the most recent successful SIP REGISTER 200-OK, if any.
    /// </summary>
    public DateTime? SipLastConnectedAt => _sipTransport?.LastConnectedAt;

    /// <summary>
    /// Whether the last cookie health check passed (cookies are still accepted by Google).
    /// </summary>
    public bool AreCookiesValid => _areCookiesValid;

    /// <summary>
    /// Age (seconds) of the current rotating freshness cookies (__Secure-1PSIDTS/3PSIDTS)
    /// based on when they were last loaded or refreshed. Null if no cookie set is loaded.
    /// Google rotates PSIDTS on its own cadence (minutes–hours); a large age is a hint that
    /// the next request may 401 with SESSION_COOKIE_INVALID even if the periodic health
    /// check last passed. Used to make /api/gvbridge/status's cookiesValid less misleading.
    /// </summary>
    public long? PsidtsAgeSeconds =>
        _psidtsRefreshedAt is { } refreshed
            ? (long)Math.Max(0, (DateTime.UtcNow - refreshed).TotalSeconds)
            : null;

    /// <summary>
    /// When the current cookie set was loaded into the adapter (set during ActivateAsync or ReloadCookiesAsync).
    /// </summary>
    public DateTime? LoadedAt { get; private set; }

    /// <summary>
    /// Timestamp of the last health check call (set in RunHealthCheckAsync and during activation).
    /// </summary>
    public DateTime? LastValidatedAt { get; private set; }

    /// <summary>
    /// The currently loaded cookie set (read-only access for status queries).
    /// </summary>
    internal GvCookieSet? CurrentCookieSet => _cookieSet;

    /// <summary>
    /// The cookie store used by the adapter (may be null before ActivateAsync).
    /// </summary>
    internal GvCookieStore? CookieStore => _cookieStore;

    public event Action<bool>? OnAvailabilityChanged;
    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnswered;
    public event Action? OnCallEnded;
    public event Action<string>? OnDtmfReceived;

    public GVApiAdapter(
        IOptions<GVBridgeConfig> config,
        ILogger<GVApiAdapter> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Inject audio bridge to avoid circular DI. Called after construction by the DI wiring layer.
    /// </summary>
    public void SetAudioBridge(GVAudioBridgeService audioBridge)
    {
        _audioBridge = audioBridge;
    }

    /// <summary>
    /// Called by CallManager after the HT801 200 OK SDP is parsed.
    /// Stores the negotiated RTP details so StartAsync can use them.
    /// </summary>
    /// <param name="ht801Port">HT801's RTP port from its SDP answer.</param>
    /// <param name="ht801Ip">HT801's IP from its SDP answer.</param>
    /// <param name="invitePort">The local RTP port we advertised in the INVITE SDP.</param>
    public void SetNegotiatedRtpDetails(int? ht801Port, string? ht801Ip, int? invitePort)
    {
        _negotiatedHt801RtpPort = ht801Port;
        _negotiatedHt801RtpIp = ht801Ip;
        _inviteRtpPort = invitePort;
        _logger.LogInformation(
            "GVApiAdapter received negotiated RTP details — HT801={Ip}:{Port}, invitePort={InvitePort}",
            ht801Ip ?? "(null)", ht801Port?.ToString() ?? "(null)", invitePort?.ToString() ?? "(null)");
    }

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("GVApiAdapter activating...");

        // 1. Load encryption key: prefer key file (written by CookieRetriever), fallback to config
        string encryptionKeyBase64;
        var keyFilePath = _config.CookieKeyFilePath;
        if (!string.IsNullOrEmpty(keyFilePath) && File.Exists(keyFilePath))
        {
            var keyBytes = await File.ReadAllBytesAsync(keyFilePath);
            encryptionKeyBase64 = Convert.ToBase64String(keyBytes);
            _logger.LogDebug("Loaded encryption key from {Path}", keyFilePath);
        }
        else if (!string.IsNullOrEmpty(_config.CookieEncryptionKey))
        {
            encryptionKeyBase64 = _config.CookieEncryptionKey;
        }
        else
        {
            _logger.LogError("No cookie encryption key found. Run 'gv-login' first.");
            SetAvailable(false);
            return;
        }

        _cookieStore = new GvCookieStore(_config.CookieFilePath, encryptionKeyBase64);
        _cookieSet = await _cookieStore.LoadAsync();
        LoadedAt = _cookieSet != null ? DateTime.UtcNow : null;
        _psidtsRefreshedAt = _cookieSet != null ? DateTime.UtcNow : null;

        if (_cookieSet == null || string.IsNullOrEmpty(_cookieSet.Sapisid))
        {
            _logger.LogWarning("GVApi: No valid cookies found at {Path} — adapter unavailable. " +
                "Run the cookie extraction tool to import cookies.", _config.CookieFilePath);
            SetAvailable(false);
            return;
        }

        // 2. Create authenticated HttpClient (cookies loaded from store)
        var handler = new GvHttpClientHandler(() =>
            Task.FromResult(_cookieSet!));
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = new Uri("https://clients6.google.com/")
        };

        // 3. Create account client for health checks
        _accountClient = new GvAccountClient(
            _httpClient, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvAccountClient>());

        // 4. Health check to verify cookies work
        var healthy = await _accountClient.IsHealthyAsync(ct);
        _areCookiesValid = healthy;
        LastValidatedAt = DateTime.UtcNow;
        if (!healthy)
        {
            _logger.LogWarning("GVApi: Initial health check failed — cookies may be expired");
            SetAvailable(false);
            return;
        }

        // 5. Create SIP transport for call signaling + DTLS-SRTP audio
        var httpClientFactory = new SingleHttpClientFactory(_httpClient);
        var credProvider = new GvSipCredentialProvider(
            httpClientFactory, _config,
            _loggerFactory.CreateLogger<GvSipCredentialProvider>());

        _sipTransport = new GvSipTransport(
            _loggerFactory.CreateLogger<GvSipTransport>(),
            () => credProvider.GetCredentialsAsync(),
            _loggerFactory);

        _sipTransport.IncomingCallReceived += HandleSipIncomingCall;
        _sipTransport.CallStatusChanged += (_, e) =>
        {
            if (e.NewStatus == CallStatusType.Active)
                OnCallAnswered?.Invoke();
            else if (e.NewStatus == CallStatusType.Completed)
                OnCallEnded?.Invoke();
        };

        // 6. Register SIP transport with Google Voice (enables incoming + outgoing calls)
        try
        {
            await _sipTransport.EnsureRegisteredAsync(ct);
            _logger.LogInformation("GVApi: SIP registered with Google Voice");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: SIP registration failed — will retry on first call");
        }

        // 7. Start periodic health check timer
        var intervalMs = _config.CookieHealthCheckIntervalMinutes * 60 * 1000;
        _healthCheckTimer = new Timer(OnHealthCheckTimer, null, intervalMs, intervalMs);

        SetAvailable(true);
        _logger.LogInformation("GVApiAdapter activated — SIP transport ready");
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("GVApiAdapter deactivating...");

        // Stop health check timer
        if (_healthCheckTimer != null)
        {
            await _healthCheckTimer.DisposeAsync();
            _healthCheckTimer = null;
        }

        // Disconnect and dispose SIP transport (releases WebSocket + Opus codecs)
        if (_sipTransport != null)
        {
            _sipTransport.IncomingCallReceived -= HandleSipIncomingCall;
            await _sipTransport.DisposeAsync();
            _sipTransport = null;
        }

        // Dispose HttpClient
        _httpClient?.Dispose();
        _httpClient = null;

        _accountClient = null;
        _cookieSet = null;
        _cookieStore = null;
        _areCookiesValid = false;
        LoadedAt = null;
        LastValidatedAt = null;
        _psidtsRefreshedAt = null;
        Interlocked.Exchange(ref _activeCallId, null);

        SetAvailable(false);
        _logger.LogInformation("GVApiAdapter deactivated");
    }

    /// <summary>
    /// Reload cookies from the store without a full deactivate/activate cycle.
    /// Updates the in-memory cookie set and re-creates the HttpClient handler.
    /// If the adapter hasn't been activated yet, this is a no-op.
    /// </summary>
    public async Task<bool> ReloadCookiesAsync(CancellationToken ct = default)
    {
        if (_cookieStore == null)
        {
            _logger.LogWarning("ReloadCookiesAsync: adapter not activated, cannot reload");
            return false;
        }

        var newCookies = await _cookieStore.LoadAsync();
        if (newCookies == null || string.IsNullOrEmpty(newCookies.Sapisid))
        {
            _logger.LogWarning("ReloadCookiesAsync: no valid cookies in store");
            return false;
        }

        _cookieSet = newCookies;
        LoadedAt = DateTime.UtcNow;
        _psidtsRefreshedAt = DateTime.UtcNow;

        // Re-create authenticated HttpClient with updated cookies
        _httpClient?.Dispose();
        var handler = new GvHttpClientHandler(() =>
            Task.FromResult(_cookieSet!));
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = new Uri("https://clients6.google.com/")
        };

        // Re-create account client with new HttpClient
        _accountClient = new GvAccountClient(
            _httpClient, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvAccountClient>());

        // Verify the new cookies work
        var healthy = await _accountClient.IsHealthyAsync(ct);
        _areCookiesValid = healthy;
        LastValidatedAt = DateTime.UtcNow;

        if (healthy && !IsAvailable)
        {
            SetAvailable(true);
            _logger.LogInformation("ReloadCookiesAsync: cookies valid, adapter now available");
        }
        else if (!healthy)
        {
            _logger.LogWarning("ReloadCookiesAsync: new cookies failed health check");
        }

        return healthy;
    }

    public async Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("GVApiAdapter is not available");

        var result = await _sipTransport!.InitiateAsync(e164Number, ct);
        if (!result.Success)
            throw new InvalidOperationException($"SIP INVITE failed: {result.ErrorMessage}");

        Interlocked.Exchange(ref _activeCallId, result.CallId);
        _logger.LogInformation("Placed call {CallId} to {Number}", result.CallId, e164Number);
        return result.CallId;
    }

    public Task AnswerCallAsync(CancellationToken ct = default)
    {
        // No-op: answering is SIP-driven. The actual answer happens in
        // OnCallAnsweredOnRotaryPhoneAsync when the handset is lifted.
        _logger.LogDebug("AnswerCallAsync called (no-op, SIP-driven)");
        return Task.CompletedTask;
    }

    public async Task HangUpAsync(CancellationToken ct = default)
    {
        var callId = Interlocked.Exchange(ref _activeCallId, null);
        if (callId != null && _sipTransport != null)
        {
            try { await _sipTransport.HangupAsync(callId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "SIP BYE failed"); }
        }
    }

    public async Task OnCallAnsweredOnRotaryPhoneAsync()
    {
        _logger.LogInformation("Rotary phone answered — starting audio bridge with negotiated ports " +
            "(HT801={Ip}:{Port}, localBind={LocalPort})",
            _negotiatedHt801RtpIp ?? "(config)", _negotiatedHt801RtpPort?.ToString() ?? "(config)",
            _inviteRtpPort?.ToString() ?? "(config)");

        if (_audioBridge != null && _sipTransport != null && _activeCallId != null)
        {
            _audioBridge.SetSipTransport(_sipTransport, _activeCallId);
            await _audioBridge.StartAsync(
                remoteRtpPort: _negotiatedHt801RtpPort,
                remoteRtpAddress: _negotiatedHt801RtpIp,
                localRtpPort: _inviteRtpPort);
        }
    }

    public async Task OnCallHungUpAsync()
    {
        // Capture call ID FIRST before stopping audio bridge, because stopping the bridge
        // clears its own internal reference (not ours, but we want this explicit).
        var callId = Interlocked.Exchange(ref _activeCallId, null);
        _logger.LogInformation(
            "Call hung up — tearing down media immediately (callId={CallId}, sipTransport={HasTransport})",
            callId ?? "(null)", _sipTransport != null);

        // Step 1: Stop the audio bridge FIRST — halts RTP to/from HT801
        if (_audioBridge != null)
            await _audioBridge.StopAsync();

        // Clear negotiated RTP details for this call
        _negotiatedHt801RtpPort = null;
        _negotiatedHt801RtpIp = null;
        _inviteRtpPort = null;

        // Step 2: Close the DTLS-SRTP session and send SIP BYE to Google Voice.
        // HangupAsync closes the peer connection (DTLS close_notify) BEFORE sending
        // BYE, so Google's RTP timeout starts immediately even though our BYE is
        // silently ignored (known interop issue — see KNOWN-ISSUES.md).
        if (callId != null && _sipTransport != null)
        {
            _logger.LogInformation(
                "Initiating GV media teardown + SIP BYE for call {CallId}", callId);
            try { await _sipTransport.HangupAsync(callId); }
            catch (Exception ex) { _logger.LogWarning(ex, "GV hangup failed for call {CallId}", callId); }
        }
        else
        {
            _logger.LogWarning("Cannot send GV SIP BYE — callId={CallId}, sipTransport={HasTransport}",
                callId ?? "(null)", _sipTransport != null);
        }
    }

    private void HandleSipIncomingCall(object? sender, IncomingCallEventArgs e)
    {
        Interlocked.Exchange(ref _activeCallId, e.CallInfo.CallId);
        _logger.LogInformation("SIP incoming call from {Number}", e.CallInfo.CallerNumber);
        OnIncomingCall?.Invoke(e.CallInfo.CallerNumber);
    }

    private void OnHealthCheckTimer(object? state)
    {
        _ = RunHealthCheckAsync();
    }

    private async Task RunHealthCheckAsync()
    {
        try
        {
            if (_accountClient == null) return;

            var healthy = await _accountClient.IsHealthyAsync();
            _areCookiesValid = healthy;
            LastValidatedAt = DateTime.UtcNow;
            if (!healthy)
            {
                _logger.LogWarning("GVApi: periodic health check failed — marking unavailable");
                SetAvailable(false);
            }
            else if (!IsAvailable)
            {
                _logger.LogInformation("GVApi: health check recovered — marking available");
                SetAvailable(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: health check error");
        }
    }

    private void SetAvailable(bool available)
    {
        if (IsAvailable != available)
        {
            IsAvailable = available;
            _logger.LogInformation("GVApi availability changed: {Available}", available);
            OnAvailabilityChanged?.Invoke(available);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _healthCheckTimer?.Dispose();
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Minimal IHttpClientFactory adapter that returns a pre-built HttpClient.
    /// Used to bridge the existing cookie-authenticated HttpClient into the
    /// GvSipCredentialProvider which expects IHttpClientFactory.
    /// </summary>
    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
