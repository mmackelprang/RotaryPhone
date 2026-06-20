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
public class GVApiAdapter : ICallAdapter, IGvAuthenticatedClientProvider, IDisposable
{
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GVApiAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Set via SetAudioBridge() to avoid circular DI
    private GVAudioBridgeService? _audioBridge;

    // Set via SetCookieExtractor() — used by the auto-recovery ladder to pull fresh cookies
    // from the box's logged-in Chrome (the same lever as the manual refresh-from-browser).
    private ICdpCookieExtractor? _cdpExtractor;

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

    // Last time the adapter was fully healthy (cookies valid AND SIP registered), set by the watchdog.
    private DateTime? _lastHealthyAt;

    // Browser-less PSIDTS refresh (RotateCookies). Injected for tests; lazily built otherwise.
    private ICookieRotator? _cookieRotator;
    private HttpClient? _rotatorHttpClient;

    // Single-flight guard so concurrent AuthenticationFailed events don't stampede the refresh.
    private int _refreshingCookies;

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
    /// True when GVApi IS the active/available path but is NOT fully usable — cookies invalid OR
    /// SIP not registered. Gated on <see cref="IsAvailable"/> so an inactive adapter (startup, or
    /// while BluetoothHfp/SipTrunk is the active mode) doesn't raise a permanent false alarm; that
    /// state is already conveyed by <c>available:false</c>. Surfaced honestly so the dashboard can
    /// see real degradation early (the 2026-06-19 outage was invisible because status lied).
    /// </summary>
    public bool Degraded => IsAvailable && !(_areCookiesValid && (_sipTransport?.IsRegistered ?? false));

    /// <summary>
    /// UTC time the adapter was last fully healthy (cookies valid AND SIP registered), per the
    /// periodic watchdog. Null if it has not been healthy since activation.
    /// </summary>
    public DateTime? LastHealthyAt => _lastHealthyAt;

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

    // --- IGvAuthenticatedClientProvider (seam for PR2/PR3 read clients) ---

    /// <summary>
    /// The current authenticated HttpClient or null if unavailable. Fetched live (not cached by
    /// callers) so cookie rotation/reload that swaps _httpClient propagates — same contract as the
    /// internal SingleHttpClientFactory used by the SIP credential provider.
    /// </summary>
    public HttpClient? GetAuthenticatedClient() => IsAvailable ? _httpClient : null;

    /// <inheritdoc />
    public string ApiBaseUrl => _config.GvApiBaseUrl;

    /// <inheritdoc />
    public string ApiKey => _config.GvApiKey;

    public event Action<bool>? OnAvailabilityChanged;
    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnswered;
    public event Action? OnCallEnded;
    public event Action<string>? OnDtmfReceived;

    public GVApiAdapter(
        IOptions<GVBridgeConfig> config,
        ILogger<GVApiAdapter> logger,
        ILoggerFactory loggerFactory)
        : this(config, logger, loggerFactory, cookieRotator: null)
    {
    }

    /// <summary>
    /// Test/extensibility constructor allowing a custom <see cref="ICookieRotator"/> to be
    /// injected (defaults to a best-effort <see cref="GvCookieRotator"/> built lazily).
    /// </summary>
    internal GVApiAdapter(
        IOptions<GVBridgeConfig> config,
        ILogger<GVApiAdapter> logger,
        ILoggerFactory loggerFactory,
        ICookieRotator? cookieRotator)
    {
        _config = config.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _cookieRotator = cookieRotator;
    }

    /// <summary>
    /// Inject audio bridge to avoid circular DI. Called after construction by the DI wiring layer.
    /// </summary>
    public void SetAudioBridge(GVAudioBridgeService audioBridge)
    {
        _audioBridge = audioBridge;
    }

    /// <summary>
    /// Inject the CDP cookie extractor (avoids circular DI). Enables the auto-recovery ladder to
    /// pull fresh cookies from the box's logged-in Chrome. Wired by the DI layer after construction.
    /// </summary>
    public void SetCookieExtractor(ICdpCookieExtractor extractor)
    {
        _cdpExtractor = extractor;
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

        // 5. Create SIP transport for call signaling + DTLS-SRTP audio.
        // Resolve _httpClient INDIRECTLY (via the field) so cookie rotation / reload that swaps
        // the field (ReloadCookiesAsync, TryRotateCookiesAsync) propagates to the cred provider's
        // next sipregisterinfo/get — otherwise it would keep the OLD disposed client and throw
        // ObjectDisposedException, breaking the 401-recovery reconnect this PR adds.
        var httpClientFactory = new SingleHttpClientFactory(() => _httpClient!);
        var credProvider = new GvSipCredentialProvider(
            httpClientFactory, _config,
            _loggerFactory.CreateLogger<GvSipCredentialProvider>());

        _sipTransport = new GvSipTransport(
            _loggerFactory.CreateLogger<GvSipTransport>(),
            () => credProvider.GetCredentialsAsync(),
            _loggerFactory);

        // Escalate real auth failures (post-Digest 401/403, or 401/403 from sipregisterinfo/get)
        // to a cookie refresh. NOT triggered by plain network drops.
        _sipTransport.AuthenticationFailed += HandleAuthenticationFailed;

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
            _sipTransport.AuthenticationFailed -= HandleAuthenticationFailed;
            await _sipTransport.DisposeAsync();
            _sipTransport = null;
        }

        // Dispose rotator HttpClient (if we built one)
        _rotatorHttpClient?.Dispose();
        _rotatorHttpClient = null;

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

    /// <summary>
    /// Auth-failure escalation handler. Fired by the transport ONLY on a real auth rejection
    /// (post-Digest 401/403 or 401/403 from sipregisterinfo/get), never on a plain network drop.
    /// Runs the recovery ladder, then lets the transport's reconnect backoff pick up the refreshed
    /// creds on its next attempt.
    /// </summary>
    private void HandleAuthenticationFailed(object? sender, AuthenticationFailedEventArgs e)
        => TriggerCookieRecovery(e.Reason);

    /// <summary>
    /// Single-flight entry into the cookie-recovery ladder. Both the transport's AuthenticationFailed
    /// event and the periodic watchdog funnel through here so only one recovery runs at a time.
    /// </summary>
    private void TriggerCookieRecovery(string reason)
    {
        if (Interlocked.CompareExchange(ref _refreshingCookies, 1, 0) != 0)
            return;

        _ = RecoverFromAuthFailureAsync(reason);
    }

    private async Task RecoverFromAuthFailureAsync(string reason)
    {
        try
        {
            _logger.LogWarning("GVApi: auth/registration recovery ({Reason})", reason);
            _areCookiesValid = false;

            // Rung 1: browser-less RotateCookies refresh of the rotating PSIDTS from the stored
            // long-lived __Secure-1PSID. Best-effort; falls through on any failure.
            if (_config.EnableCookieRotation && _cookieSet != null && await TryRotateCookiesAsync())
            {
                _logger.LogInformation("GVApi: RotateCookies refreshed PSIDTS");
                await ForceReRegisterAsync();
                return;
            }

            // Rung 2: re-read cookies from disk (an out-of-band refresh may have updated them).
            if (await ReloadCookiesAsync())
            {
                _logger.LogInformation("GVApi: reloaded cookies from disk");
                await ForceReRegisterAsync();
                return;
            }

            // Rung 3: pull fresh cookies from the box's logged-in Chrome via CDP and adopt them
            // in-process — the automatic equivalent of the manual refresh-from-browser that
            // resolved the 2026-06-19 incident. No service restart required.
            if (await TryCdpRefreshAsync())
            {
                _logger.LogInformation("GVApi: refreshed cookies from browser via CDP");
                await ForceReRegisterAsync();
                return;
            }

            _logger.LogWarning(
                "GVApi: all cookie-recovery rungs failed. The box's Chrome login may be dead — " +
                "re-login at voice.google.com so the next CDP refresh can pick up a fresh session.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: error during auth/registration recovery");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshingCookies, 0);
        }
    }

    /// <summary>
    /// Recovery rung 3: extract fresh cookies from the box's logged-in Chrome via CDP, persist them,
    /// and adopt them in-process (<see cref="ReloadCookiesAsync"/> swaps the HttpClient — no restart).
    /// The extractor is optional; returns false if it was never wired or extraction/validation fails.
    /// </summary>
    private async Task<bool> TryCdpRefreshAsync()
    {
        if (_cdpExtractor == null || _cookieStore == null)
            return false;

        try
        {
            var result = await _cdpExtractor.ExtractAsync(_config.ChromeCdpPort, "voice.google.com");
            if (!result.Success || result.Cookies == null)
            {
                _logger.LogWarning("GVApi: CDP cookie refresh failed: {Status} {Error}", result.Status, result.Error);
                return false;
            }

            await _cookieStore.SaveAsync(result.Cookies);
            return await ReloadCookiesAsync(); // adopt in-memory + re-validate against Google
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: CDP cookie refresh threw");
            return false;
        }
    }

    /// <summary>
    /// Force the SIP transport to re-register immediately with the current (freshly refreshed)
    /// credentials instead of waiting out the reconnect backoff. In-process — no service restart.
    /// </summary>
    private async Task ForceReRegisterAsync()
    {
        if (_sipTransport == null)
            return;

        try
        {
            await _sipTransport.EnsureRegisteredAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: forced re-register after cookie refresh failed (backoff will retry)");
        }
    }

    /// <summary>
    /// Attempt the browser-less RotateCookies refresh, overlay the fresh PSIDTS onto the
    /// in-memory + on-disk cookie set, and re-create the authenticated HttpClient. Returns
    /// true only if cookies were actually rotated and re-verified healthy.
    /// </summary>
    private async Task<bool> TryRotateCookiesAsync()
    {
        var current = _cookieSet;
        if (current == null)
            return false;

        var rotator = _cookieRotator ??= BuildDefaultCookieRotator();

        var result = await rotator.RotateAsync(current);
        if (!result.Rotated)
            return false;

        // Overlay the refreshed PSIDTS so ToCookieHeader() stops replaying the stale values.
        var refreshed = current.WithRefreshedPsidts(result.Psidts1, result.Psidts3);
        _cookieSet = refreshed;
        _psidtsRefreshedAt = DateTime.UtcNow;

        // Persist so a restart / other paths pick up the fresh cookies.
        if (_cookieStore != null)
        {
            try { await _cookieStore.SaveAsync(refreshed); }
            catch (Exception ex) { _logger.LogWarning(ex, "GVApi: failed to persist rotated cookies"); }
        }

        // Re-create the authenticated HttpClient with the refreshed cookie set.
        _httpClient?.Dispose();
        var handler = new GvHttpClientHandler(() => Task.FromResult(_cookieSet!));
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = new Uri("https://clients6.google.com/")
        };
        _accountClient = new GvAccountClient(
            _httpClient, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvAccountClient>());

        var healthy = await _accountClient.IsHealthyAsync();
        _areCookiesValid = healthy;
        LastValidatedAt = DateTime.UtcNow;
        return healthy;
    }

    private ICookieRotator BuildDefaultCookieRotator()
    {
        // RotateCookies sends its own Cookie header (no SAPISIDHASH), so use a plain client.
        _rotatorHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return new GvCookieRotator(_rotatorHttpClient, _loggerFactory.CreateLogger<GvCookieRotator>());
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

            var registered = _sipTransport?.IsRegistered ?? false;

            if (healthy && registered)
            {
                // Fully healthy — record it and (re)mark available if we were down.
                _lastHealthyAt = DateTime.UtcNow;
                if (!IsAvailable)
                {
                    _logger.LogInformation("GVApi: watchdog — healthy again, marking available");
                    SetAvailable(true);
                }
                return;
            }

            if (!healthy)
            {
                // Cookies rejected by Google → run the full recovery ladder (rotate/reload/CDP).
                _logger.LogWarning("GVApi: watchdog — cookies invalid, triggering recovery");
                SetAvailable(false);
                TriggerCookieRecovery("watchdog: cookies invalid");
            }
            else if (Volatile.Read(ref _refreshingCookies) == 0)
            {
                // Cookies fine but SIP is not registered (e.g., a stuck/declined registration like the
                // 2026-06-19 incident). Skip if a recovery is already in flight (it will re-register);
                // otherwise just force a clean re-register without churning cookies. If that re-register
                // turns out to fail auth, it escalates to the full ladder via AuthenticationFailed.
                _logger.LogWarning("GVApi: watchdog — cookies valid but SIP not registered, forcing re-register");
                _ = ForceReRegisterAsync();
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
    /// Minimal IHttpClientFactory adapter that resolves the CURRENT HttpClient via a factory
    /// lambda. Holding a <see cref="Func{HttpClient}"/> (rather than a captured instance) means
    /// every <see cref="CreateClient"/> call picks up the latest <c>_httpClient</c> after cookie
    /// rotation/reload swaps it — so the cred provider never holds a disposed client.
    /// </summary>
    private sealed class SingleHttpClientFactory(Func<HttpClient> clientFactory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => clientFactory();
    }
}
