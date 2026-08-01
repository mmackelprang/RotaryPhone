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
    private Timer? _cookieRefreshTimer;

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

    // Single-flight recovery. A second caller arriving mid-recovery AWAITS the in-flight run rather
    // than being turned away — during a blackout the poller and several RadioConsole requests hit 401
    // within milliseconds and must all ride one refresh. Guarded by _recoveryLock.
    private readonly object _recoveryLock = new();
    private Task<bool>? _recoveryTask;

    // Failure-only cooldown: after a ladder run that FAILED, suppress new runs for this long so a real
    // Google outage can't drive RotateCookies at the poll rate (the 2026-06-19 storm shape).
    private DateTime _recoveryCooldownUntilUtc = DateTime.MinValue;

    // Serializes the PUBLIC ActivateAsync / DeactivateAsync bodies, so a cron-driven re-activation
    // cannot interleave with a mode switch — or with a second re-activation — and leave the adapter
    // half torn down (transport disposed, clients not yet rebuilt).
    //
    // DEADLOCK RULE: taken by the PUBLIC entry points ONLY. ActivateCoreAsync calls the teardown
    // internally (TryDeactivateForReactivationAsync → TearDownGenerationAsync), so the core paths
    // must NEVER re-take it. Verified safe: nothing inside this class calls the public
    // ActivateAsync/DeactivateAsync (TryCdpRefreshAsync, ReloadCookiesAsync and
    // RecoverFromAuthFailureAsync all go through ReloadCookiesAsync, which is ungated), and the only
    // external caller — CallAdapterRegistry.SwitchModeAsync — deactivates the OUTGOING adapter and
    // activates the INCOMING one, never both on the same instance. GvCookieManager reaches the
    // adapter only through that registry.
    private readonly SemaphoreSlim _activationGate = new(1, 1);

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
    /// Cookies are valid only if the last PROBE passed AND no real data-plane call has since been
    /// rejected for auth. The probe alone is a 30-minute-stale reading of a DIFFERENT endpoint
    /// (threadinginfo/get) than the one that fails (api2thread/list) — spec F5.
    /// </summary>
    public bool AreCookiesValid => _areCookiesValid && !AuthBlackout;

    // Data-plane truth (spec §4.3). A health field derived from a PROBE reports healthy straight
    // through an outage — the 2026-07-31 blackout reported cookiesValid:true while api2thread/list
    // was returning 401. These are written by the actual GV calls, not by threadinginfo/get.
    private DateTime? _lastApiSuccessAtUtc;
    private DateTime? _lastApiAuthFailureAtUtc;

    /// <summary>UTC of the last 2xx from a real GV data-plane call.</summary>
    public DateTime? LastApiSuccessAt => _lastApiSuccessAtUtc;

    /// <summary>UTC of the last 401/403 from a real GV data-plane call.</summary>
    public DateTime? LastApiAuthFailureAt => _lastApiAuthFailureAtUtc;

    /// <summary>
    /// True when the most recent real GV data-plane call was rejected for auth and nothing has
    /// succeeded since. This is the field RadioConsole's "Google Voice is reconnecting" banner
    /// should bind to — NOT <see cref="IsAvailable"/>, which stays true by design (spec §4.3).
    /// </summary>
    public bool AuthBlackout =>
        _lastApiAuthFailureAtUtc is { } fail &&
        (_lastApiSuccessAtUtc is not { } ok || fail > ok);

    /// <summary>Called by the GV clients after every real data-plane call. Cheap, lock-free.</summary>
    public void RecordApiOutcome(bool success, bool authFailure)
    {
        if (success) _lastApiSuccessAtUtc = DateTime.UtcNow;
        else if (authFailure) _lastApiAuthFailureAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// True when GVApi IS the active/available path but is NOT fully usable — cookies invalid OR
    /// SIP not registered. Gated on <see cref="IsAvailable"/> so an inactive adapter (startup, or
    /// while BluetoothHfp/SipTrunk is the active mode) doesn't raise a permanent false alarm; that
    /// state is already conveyed by <c>available:false</c>. Surfaced honestly so the dashboard can
    /// see real degradation early (the 2026-06-19 outage was invisible because status lied).
    /// </summary>
    /// <remarks>
    /// Derives from the <see cref="AreCookiesValid"/> PROPERTY (not the raw probe field), so an
    /// auth blackout on the data plane makes <c>degraded</c> honest for free — spec §4.3.
    /// </remarks>
    public bool Degraded => IsAvailable && !(AreCookiesValid && (_sipTransport?.IsRegistered ?? false));

    /// <summary>
    /// UTC time the adapter was last fully healthy (cookies valid AND SIP registered), per the
    /// periodic watchdog. Null if it has not been healthy since activation.
    /// </summary>
    public DateTime? LastHealthyAt => _lastHealthyAt;

    /// <summary>
    /// UTC time a 603/403 REGISTER-throttle cooldown ends, or null when not throttled. During a
    /// cooldown the transport sends NO REGISTER so Google's account-level throttle can cool; the
    /// status endpoint surfaces this honestly alongside <see cref="Degraded"/>=true.
    /// </summary>
    public DateTime? ThrottledUntil => _sipTransport?.ThrottledUntil;

    /// <summary>Human-readable reason for the active throttle cooldown, or null when not throttled.</summary>
    public string? ThrottleReason => _sipTransport?.ThrottleReason;

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

    /// <summary>
    /// Public activation entry point. Serialized against <see cref="DeactivateAsync"/> and against a
    /// concurrent activation by <c>_activationGate</c>; all the real work lives in
    /// <see cref="ActivateCoreAsync"/>, which must NOT take the gate again (see the field remark).
    /// </summary>
    public async Task ActivateAsync(CancellationToken ct = default)
    {
        await _activationGate.WaitAsync(ct);
        try
        {
            await ActivateCoreAsync(ct);
        }
        finally
        {
            _activationGate.Release();
        }
    }

    private async Task ActivateCoreAsync(CancellationToken ct)
    {
        _logger.LogInformation("GVApiAdapter activating...");

        // Resolve the key and load the INCOMING cookie set FIRST, into locals, mutating no field.
        // The re-entrancy decision below has to see the incoming credentials to decide whether the
        // live SIP transport may be kept; it used to sit above this block, where it structurally
        // could not. A failure here leaves every field untouched and falls straight through to the
        // unchanged teardown path — same observable behaviour as before this restructure.
        var encryptionKeyBase64 = await TryResolveEncryptionKeyAsync();
        GvCookieStore? incomingStore = null;
        GvCookieSet? incomingCookies = null;
        if (encryptionKeyBase64 != null)
        {
            incomingStore = new GvCookieStore(_config.CookieFilePath, encryptionKeyBase64);
            incomingCookies = await incomingStore.LoadAsync();
        }

        // ---------------------------------------------------------------- the re-entrancy decision
        //
        // RE-ENTRANCY (F6/F7). CallAdapterRegistry.SwitchModeAsync skips DeactivateAsync when the
        // mode is unchanged, so the box-side cron re-enters here on the LIVE adapter every ~20 min.
        // Before the F6 fix each pass LEAKED an armed 30-min Timer, an HttpClient and a whole
        // GvSipTransport; the first fix cured the leak by tearing the generation down every time,
        // which churned a perfectly healthy, registered WebSocket on every cron fire. This is the
        // conditional form: churn nothing that is still healthy.
        //
        // RACE SAFETY — READ BEFORE EDITING. Paths A and B never touch _sipTransport, so an INVITE
        // arriving mid-re-activation cannot catch a half-disposed transport: there is no teardown to
        // race. DO NOT reintroduce a teardown (or a re-register) into either branch.
        if (incomingCookies is { } incoming && !string.IsNullOrEmpty(incoming.Sapisid) && CanReuseTransport)
        {
            if (CredentialsUnchanged(_cookieSet, incoming))
            {
                // A. Nothing changed and nothing is broken. Rebuild NOTHING — not the transport, not
                // the HttpClient, and above all not the timers: re-arming a fresh 30-minute health
                // timer on every cron fire is F7, the starvation bug this PR exists to fix.
                _logger.LogInformation(
                    "GVApi: re-activation is a no-op — credentials unchanged and SIP transport healthy; " +
                    "reusing everything");

                // The transport is registered and we are holding exactly the credentials already in
                // use, so a re-activation must not leave the adapter marked unavailable — the
                // refresher's contract is "after this returns, GVApi is the live path". Nothing is
                // probed here, so this only ever RESTORES availability; Degraded/AuthBlackout still
                // report any real data-plane failure honestly (spec §4.3).
                if (!IsAvailable) SetAvailable(true);
                return;
            }

            // B. New credentials, healthy transport. Adopt the credentials, keep the transport AND
            // the timers. Deliberately does NOT re-register: a PSIDTS rotation does not invalidate a
            // live SIP registration, and re-registering on the cron's cadence would re-create the
            // 2026-06-19 REGISTER-storm risk (spec §4.1). The transport resolves _httpClient lazily
            // through SingleHttpClientFactory and caches no credentials, so the swap below reaches
            // its NEXT sipregisterinfo/get with no rebuild at all.
            _logger.LogInformation(
                "GVApi: re-activation adopting new credentials — SIP transport is healthy, keeping it");
            _cookieStore = incomingStore;
            await ReloadCookiesAsync(ct);
            return;
        }

        // C. No transport, or one that is not registered — the full teardown + rebuild, unchanged.
        if (_sipTransport != null || _healthCheckTimer != null)
        {
            if (_activeCallId != null)
            {
                // A call is up. Adopt the new cookies but do NOT tear down the transport — that
                // would drop the live call.
                _logger.LogInformation(
                    "GVApi: re-activating during an active call — refreshing cookies only, keeping SIP transport");
                await RefreshAuthenticatedClientsAsync(ct);
                return;
            }

            _logger.LogInformation("GVApi: re-activating — tearing down the previous generation first");
            if (!await TryDeactivateForReactivationAsync(ct))
            {
                // A call started while we were tearing down (the timer disposals inside can block
                // for SECONDS on an in-flight health probe — see TryDeactivateForReactivationAsync).
                // Nothing irreversible happened yet: restore the timers we disposed, adopt the new
                // cookies, and leave the ringing/active call — and its transport — alone.
                _logger.LogInformation(
                    "GVApi: re-activation aborted — a call started during teardown; " +
                    "keeping SIP transport and refreshing cookies only");
                StartPeriodicTimers();
                await RefreshAuthenticatedClientsAsync(ct);
                return;
            }
        }

        // 1. Adopt the encryption key / cookie set resolved above.
        if (encryptionKeyBase64 == null)
        {
            // TryResolveEncryptionKeyAsync already logged why.
            SetAvailable(false);
            return;
        }

        _cookieStore = incomingStore;
        _cookieSet = incomingCookies;
        LoadedAt = _cookieSet != null ? DateTime.UtcNow : null;
        _psidtsRefreshedAt = _cookieSet != null ? DateTime.UtcNow : null;

        if (_cookieSet == null || string.IsNullOrEmpty(_cookieSet.Sapisid))
        {
            _logger.LogWarning("GVApi: No valid cookies found at {Path} — adapter unavailable. " +
                "Run the cookie extraction tool to import cookies.", _config.CookieFilePath);
            SetAvailable(false);
            return;
        }

        // 2/3. Create the authenticated HttpClient + account client from the adopted cookie set.
        SwapAuthenticatedClients();

        // 4. Health check to verify cookies work
        var healthy = await ProbeHealthAsync(ct);
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

        // 7. Start the periodic timers (health watchdog + proactive PSIDTS refresh)
        StartPeriodicTimers();

        SetAvailable(true);
        _logger.LogInformation("GVApiAdapter activated — SIP transport ready");
    }

    /// <summary>
    /// Whether a re-activation may keep the live SIP transport instead of rebuilding it.
    /// </summary>
    /// <remarks>
    /// Transport HEALTH is the only criterion, deliberately. <see cref="GvSipTransport"/> holds a
    /// <c>Func&lt;Task&lt;SipCredentials&gt;&gt;</c> that it invokes FRESH on every register
    /// (<c>Sip/GvSipTransport.cs:1021</c>) and caches no credentials; that func closes over
    /// <see cref="GvSipCredentialProvider"/>, which resolves the HttpClient through
    /// <see cref="SingleHttpClientFactory"/> — i.e. by reading the <c>_httpClient</c> FIELD lazily,
    /// by design. A cookie swap therefore propagates to the transport with no rebuild at all, which
    /// is exactly what shipped recovery rung 2 (<see cref="ReloadCookiesAsync"/>) already relies on.
    /// <para>
    /// Adding "…and the credentials are unchanged" to this predicate would be equivalent to
    /// rebuilding UNCONDITIONALLY: the cron pulls from Chrome every 20 minutes while PSIDTS rotates
    /// every ~11, so the incoming cookie header differs on very nearly every cycle. That is the
    /// churn this predicate exists to remove.
    /// </para>
    /// <para>
    /// <c>IsRegistered</c> is <c>_registered AND IsConnected</c>, so it is already the single
    /// health signal — no separate connectivity check is needed.
    /// </para>
    /// <para>
    /// TO ADOPT THE STRICTER RULE, one line changes: the guard in <see cref="ActivateCoreAsync"/>
    /// that reads <c>… &amp;&amp; CanReuseTransport</c> becomes
    /// <c>… &amp;&amp; CanReuseTransport &amp;&amp; CredentialsUnchanged(_cookieSet, incoming)</c>.
    /// Everything with changed credentials then falls through to the full rebuild (path C) and
    /// branch B becomes unreachable. Nothing else moves.
    /// </para>
    /// </remarks>
    private bool CanReuseTransport => _sipTransport?.IsRegistered == true;

    /// <summary>
    /// Whether an incoming cookie set would put exactly the same bytes on the wire as the one
    /// already loaded. <see cref="GvCookieSet.ToCookieHeader"/> returns <c>RawCookieHeader</c>
    /// verbatim when set (the normal case on the box) and the rotating PSIDTS values live INSIDE
    /// that raw header, so the rendered header — not the typed fields — is the correct basis for
    /// "did the credentials actually change". A null current set counts as changed.
    /// </summary>
    private static bool CredentialsUnchanged(GvCookieSet? current, GvCookieSet incoming)
        => string.Equals(current?.ToCookieHeader(), incoming.ToCookieHeader(), StringComparison.Ordinal);

    /// <summary>
    /// Resolve the cookie encryption key: prefer the key file (written by CookieRetriever), fall
    /// back to config. Returns null — having logged why — when neither is available.
    /// </summary>
    private async Task<string?> TryResolveEncryptionKeyAsync()
    {
        var keyFilePath = _config.CookieKeyFilePath;
        if (!string.IsNullOrEmpty(keyFilePath) && File.Exists(keyFilePath))
        {
            var keyBytes = await File.ReadAllBytesAsync(keyFilePath);
            _logger.LogDebug("Loaded encryption key from {Path}", keyFilePath);
            return Convert.ToBase64String(keyBytes);
        }

        if (!string.IsNullOrEmpty(_config.CookieEncryptionKey))
            return _config.CookieEncryptionKey;

        _logger.LogError("No cookie encryption key found. Run 'gv-login' first.");
        return null;
    }

    /// <summary>
    /// Rebuild the authenticated <see cref="HttpClient"/> and <see cref="GvAccountClient"/> from the
    /// CURRENT <c>_cookieSet</c>, publishing the new pair BEFORE disposing the old client.
    /// </summary>
    /// <remarks>
    /// The ordering is the point. A health probe or a data-plane read can be in flight while cookies
    /// are swapped; disposing first (what this used to do) guarantees that request dies. Constructing
    /// first, assigning the fields, and disposing last narrows the window to requests ALREADY
    /// dispatched on the old client. That residual is real but unavoidable without draining, and
    /// every caller of the affected paths catches and logs the resulting fault.
    /// </remarks>
    private void SwapAuthenticatedClients()
    {
        var previous = _httpClient;

        var handler = new GvHttpClientHandler(() => Task.FromResult(_cookieSet!));
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = AuthenticatedClientBaseAddress
        };

        _httpClient = client;
        _accountClient = new GvAccountClient(
            client, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvAccountClient>());

        previous?.Dispose();
    }

    /// <summary>
    /// Public mode-switch teardown. Unconditional BY DESIGN: a genuine switch away from GVApi must
    /// fully tear down even mid-call. The re-entrancy path uses
    /// <see cref="TryDeactivateForReactivationAsync"/> instead, which can abort.
    /// Serialized against <see cref="ActivateAsync"/> by <c>_activationGate</c>; the core body is
    /// separate so the gate is taken by the public entry point ONLY.
    /// </summary>
    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        await _activationGate.WaitAsync(ct);
        try
        {
            await DeactivateCoreAsync();
        }
        finally
        {
            _activationGate.Release();
        }
    }

    private async Task DeactivateCoreAsync()
    {
        _logger.LogInformation("GVApiAdapter deactivating...");

        await DisposePeriodicTimersAsync();
        await TearDownGenerationAsync();
    }

    /// <summary>
    /// Teardown used ONLY by the <see cref="ActivateAsync"/> re-entrancy path. Identical to
    /// <see cref="DeactivateAsync"/> except that it re-checks <c>_activeCallId</c> after the timer
    /// disposals and before anything irreversible, and abandons the teardown if a call appeared.
    /// Returns false when it abandoned — the caller must then re-arm the timers via
    /// <see cref="StartPeriodicTimers"/>, since those are the only things it destroyed.
    /// </summary>
    /// <remarks>
    /// Why the re-check exists: the guard at the top of ActivateAsync tests <c>_activeCallId</c>
    /// once and then commits to a teardown, but <c>Timer.DisposeAsync()</c> does not complete until
    /// an ALREADY-RUNNING callback finishes — and <c>RunHealthCheckAsync</c> makes a live HTTP call
    /// with a 30-second client timeout. That window is seconds wide, and
    /// <c>IncomingCallReceived</c> fires while the phone is still RINGING. Without this second look
    /// a call that started ringing inside the window would have its transport disposed out from
    /// under it and be silently dropped.
    /// </remarks>
    private async Task<bool> TryDeactivateForReactivationAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("GVApiAdapter deactivating...");

        await DisposePeriodicTimersAsync();

        // Second look, at the last point where nothing unrecoverable has happened: only the two
        // timers are gone, and the caller re-arms them. The residual window between this check and
        // the transport disposal below is a few instructions wide and is irreducible without taking
        // a lock — that is accepted deliberately, consistent with the lock-free
        // Interlocked.Exchange(ref _activeCallId, …) idiom used throughout this file. It was not
        // missed; do not "fix" it by adding a lock without revisiting that idiom as a whole.
        if (_activeCallId != null)
            return false;

        await TearDownGenerationAsync();
        return true;
    }

    /// <summary>
    /// Stops the health watchdog and the proactive PSIDTS refresh timer. Reversible: the only way
    /// back is <see cref="StartPeriodicTimers"/>, which the abandoned-teardown path calls.
    /// </summary>
    private async Task DisposePeriodicTimersAsync()
    {
        // Stop health check timer
        if (_healthCheckTimer != null)
        {
            await _healthCheckTimer.DisposeAsync();
            _healthCheckTimer = null;
        }

        // Stop the proactive PSIDTS refresh timer (Task 3 makes this path actually run on the
        // re-activation the external refresher triggers, so this is what stops it accumulating).
        if (_cookieRefreshTimer != null)
        {
            await _cookieRefreshTimer.DisposeAsync();
            _cookieRefreshTimer = null;
        }
    }

    /// <summary>
    /// Irreversible half of the teardown: disposes the SIP transport and the HTTP clients and
    /// clears all per-generation state. Nothing here can be undone by re-arming a timer.
    /// </summary>
    private async Task TearDownGenerationAsync()
    {
        // Disconnect and dispose SIP transport (releases WebSocket + Opus codecs)
        if (_sipTransport != null)
        {
            _sipTransport.IncomingCallReceived -= HandleSipIncomingCall;
            _sipTransport.AuthenticationFailed -= HandleAuthenticationFailed;
            await _sipTransport.DisposeAsync();
            _sipTransport = null;
        }

        // Dispose the rotator HttpClient if WE built one — and drop the rotator with it, since it
        // holds that client and would otherwise hand a disposed instance to the next rung-1 attempt
        // after a re-activation. A rotator INJECTED via the test constructor has no
        // _rotatorHttpClient and is deliberately preserved.
        if (_rotatorHttpClient != null)
        {
            _rotatorHttpClient.Dispose();
            _rotatorHttpClient = null;
            _cookieRotator = null;
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

        // Data-plane outcome timestamps are per-generation too. Leaving them set would carry a
        // stale authBlackout:true from the torn-down generation into the freshly re-activated one
        // and mislead the dashboard until the next real GV call happened to land.
        _lastApiSuccessAtUtc = null;
        _lastApiAuthFailureAtUtc = null;

        Interlocked.Exchange(ref _activeCallId, null);

        SetAvailable(false);
        _logger.LogInformation("GVApiAdapter deactivated");
    }

    /// <summary>
    /// Mid-call re-activation path (F6/F7): adopt the refresher's new cookies WITHOUT touching the
    /// SIP transport, so a live call is not dropped. Delegates to <see cref="ReloadCookiesAsync"/>,
    /// which already performs exactly the needed sequence against the live <c>_cookieStore</c> —
    /// load from store → swap <c>_cookieSet</c> → dispose+rebuild <c>_httpClient</c> → rebuild
    /// <c>_accountClient</c> → health probe → mark available. Reused rather than duplicating
    /// ActivateAsync's construction block, which in any case cannot run here: the re-entrancy guard
    /// sits ABOVE encryption-key resolution.
    /// </summary>
    private Task<bool> RefreshAuthenticatedClientsAsync(CancellationToken ct = default)
        => ReloadCookiesAsync(ct);

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

        // Re-create the authenticated HttpClient + account client with the updated cookies. This
        // CONSTRUCTS AND PUBLISHES BEFORE DISPOSING the old client (it used to dispose first), which
        // matters because this method is shared by recovery rungs 2/3 AND by the re-activation path
        // that adopts new credentials while a health check may be in flight — see
        // SwapAuthenticatedClients for the residual.
        SwapAuthenticatedClients();

        // Verify the new cookies work
        var healthy = await ProbeHealthAsync(ct);
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
    /// Fire-and-forget entry into the recovery ladder (SIP transport + watchdog). Thin wrapper over
    /// <see cref="TryRecoverAuthAsync"/> so there is ONE ladder implementation, not two.
    /// </summary>
    private void TriggerCookieRecovery(string reason) => _ = TryRecoverAuthAsync(reason);

    /// <summary>
    /// Awaitable single-flight entry into the cookie-recovery ladder. Returns true when cookies were
    /// refreshed and re-validated. Concurrent callers share ONE run. Read paths await this and then
    /// retry once; the SIP path calls it fire-and-forget via <see cref="TriggerCookieRecovery"/>.
    /// </summary>
    public Task<bool> TryRecoverAuthAsync(string reason, CancellationToken ct = default)
    {
        lock (_recoveryLock)
        {
            if (_recoveryTask is { IsCompleted: false })
                return _recoveryTask;                       // ride the in-flight recovery

            if (DateTime.UtcNow < _recoveryCooldownUntilUtc)
                return Task.FromResult(false);              // failure cooldown active

            _recoveryTask = RecoverFromAuthFailureAsync(reason);
            return _recoveryTask;
        }
    }

    /// <summary>
    /// True while a recovery ladder run is in flight. The proactive refresh (spec §4.1) and the
    /// watchdog both consult this so neither races the reactive ladder.
    /// </summary>
    private bool IsRecoveryInFlight
    {
        get { lock (_recoveryLock) { return _recoveryTask is { IsCompleted: false }; } }
    }

    private async Task<bool> RecoverFromAuthFailureAsync(string reason)
    {
        var succeeded = false;
        try
        {
            _logger.LogWarning("GVApi: auth/registration recovery ({Reason})", reason);
            _areCookiesValid = false;

            // Rung 1: browser-less RotateCookies refresh of the rotating PSIDTS from the stored
            // long-lived __Secure-1PSID. Best-effort; falls through on any failure.
            if (_config.EnableCookieRotation && _cookieSet != null && await TryRotateCookiesAsync())
            {
                _logger.LogInformation("GVApi: RotateCookies refreshed PSIDTS");
                succeeded = true;
                MarkAvailableAfterRecovery();
                await ReRegisterUnlessThrottledAsync();
                return true;
            }

            // Rung 2: re-read cookies from disk (an out-of-band refresh may have updated them).
            if (await ReloadCookiesAsync())
            {
                _logger.LogInformation("GVApi: reloaded cookies from disk");
                succeeded = true;
                MarkAvailableAfterRecovery();
                await ReRegisterUnlessThrottledAsync();
                return true;
            }

            // Rung 3: pull fresh cookies from the box's logged-in Chrome via CDP and adopt them
            // in-process — the automatic equivalent of the manual refresh-from-browser that
            // resolved the 2026-06-19 incident. No service restart required.
            if (await TryCdpRefreshAsync())
            {
                _logger.LogInformation("GVApi: refreshed cookies from browser via CDP");
                succeeded = true;
                MarkAvailableAfterRecovery();
                await ReRegisterUnlessThrottledAsync();
                return true;
            }

            _logger.LogWarning(
                "GVApi: all cookie-recovery rungs failed. The box's Chrome login may be dead — " +
                "re-login at voice.google.com so the next CDP refresh can pick up a fresh session.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: error during auth/registration recovery");
            return false;
        }
        finally
        {
            // The shared _recoveryTask IS the single-flight guard now, so the only thing left for
            // finally is arming the failure-only cooldown. A SUCCESSFUL run arms nothing.
            if (!succeeded)
            {
                _recoveryCooldownUntilUtc =
                    DateTime.UtcNow.AddSeconds(_config.AuthRecoveryFailureCooldownSeconds);
            }
        }
    }

    /// <summary>
    /// A successful rung means we have a live authenticated client again. Without this the
    /// IsAvailable gate on <see cref="GetAuthenticatedClient"/> keeps the seam returning null until
    /// the next 30-min health tick — the PR1 review HIGH-2 window (arc tracker, open decision #6) —
    /// which would silently defeat the read-path retry this work adds.
    /// </summary>
    private void MarkAvailableAfterRecovery()
    {
        if (!IsAvailable) SetAvailable(true);
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
    /// Re-register after a successful cookie-recovery rung — UNLESS the transport is in a 603/403
    /// throttle cooldown. When throttled, an immediate ForceReRegister is exactly the storm that
    /// caused the 2026-06-19 incident: the cookie refresh is a no-op (cookies were already valid),
    /// so re-registering just hammers straight back into Google's account-level throttle. The
    /// transport's RegisterAsync gate would suppress the actual REGISTER anyway, but skipping here
    /// keeps the recovery quiet and lets the transport's own reconnect loop re-register once the
    /// cooldown elapses.
    /// </summary>
    private async Task ReRegisterUnlessThrottledAsync()
    {
        if (_sipTransport?.IsThrottled == true)
        {
            _logger.LogWarning(
                "GVApi: deferring re-register — throttle cooldown active until {Until:o} ({Reason}); " +
                "the transport's reconnect loop will re-register once it cools",
                _sipTransport.ThrottledUntil, _sipTransport.ThrottleReason);
            return;
        }

        await ForceReRegisterAsync();
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

        // Re-create the authenticated HttpClient with the refreshed cookie set. Uses the shared
        // construct -> publish -> dispose-old helper: this is the PROACTIVE path, so it runs on the
        // CookieRefreshIntervalMinutes cadence (every 8 min by default) — the most frequent client
        // swap in the adapter, and therefore the one where disposing before publishing had the
        // widest chance of pulling the client out from under a concurrent caller.
        SwapAuthenticatedClients();

        var healthy = await ProbeHealthAsync();
        _areCookiesValid = healthy;
        LastValidatedAt = DateTime.UtcNow;
        return healthy;
    }

    /// <summary>
    /// Test seam: the GV health probe (threadinginfo/get). Defaults to the live GvAccountClient.
    /// Injected by tests so the recovery ladder can be exercised without talking to Google.
    /// </summary>
    internal Func<CancellationToken, Task<bool>>? HealthProbeOverride { get; set; }

    /// <summary>
    /// Origin the authenticated <see cref="HttpClient"/> is based at. Derived from
    /// <see cref="GVBridgeConfig.GvApiBaseUrl"/> rather than hard-coded, because
    /// <see cref="GvSipCredentialProvider"/> posts a RELATIVE uri
    /// (<c>voice/v1/voiceclient/sipregisterinfo/get</c>) against this BaseAddress — so with a
    /// hard-coded origin the configured API host silently governed the health probe but NOT the
    /// SIP credential fetch. With the shipped default this evaluates to exactly
    /// <c>https://clients6.google.com/</c>, so production behaviour is unchanged; it also lets a
    /// test point activation at an unroutable address and stay offline. Falls back to the historic
    /// literal if the configured value is not an absolute uri.
    /// </summary>
    private Uri AuthenticatedClientBaseAddress =>
        Uri.TryCreate(_config.GvApiBaseUrl, UriKind.Absolute, out var configured)
            ? new Uri(configured.GetLeftPart(UriPartial.Authority) + "/")
            : new Uri("https://clients6.google.com/");

    private Task<bool> ProbeHealthAsync(CancellationToken ct = default)
        => HealthProbeOverride is { } probe
            ? probe(ct)
            : (_accountClient?.IsHealthyAsync(ct) ?? Task.FromResult(false));

    private ICookieRotator BuildDefaultCookieRotator()
    {
        // RotateCookies sends its own Cookie header (no SAPISIDHASH), so use a plain client.
        _rotatorHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return new GvCookieRotator(_rotatorHttpClient, _loggerFactory.CreateLogger<GvCookieRotator>());
    }

    /// <summary>
    /// Install the periodic timers: the health watchdog and the proactive PSIDTS refresh. Extracted
    /// from <see cref="ActivateAsync"/> so the cadence wiring — including the
    /// <c>CookieRefreshIntervalMinutes: 0</c> kill switch — is unit-testable without a live
    /// activation (which needs a real cookie file and a network round-trip).
    /// </summary>
    private void StartPeriodicTimers()
    {
        var intervalMs = _config.CookieHealthCheckIntervalMinutes * 60 * 1000;
        _healthCheckTimer = new Timer(OnHealthCheckTimer, null, intervalMs, intervalMs);

        // Proactive PSIDTS refresh (spec §4.1). Rung 1 ONLY — browser-less RotateCookies. CDP
        // (rung 3) is heavy and needs the box's Chrome; it stays reserved for reactive recovery.
        if (_config.CookieRefreshIntervalMinutes > 0)
        {
            var refreshMs = _config.CookieRefreshIntervalMinutes * 60 * 1000;
            _cookieRefreshTimer = new Timer(OnCookieRefreshTimer, null, refreshMs, refreshMs);
        }
    }

    private void OnHealthCheckTimer(object? state)
    {
        _ = RunHealthCheckAsync();
    }

    private void OnCookieRefreshTimer(object? state) => _ = RunProactiveCookieRefreshAsync();

    /// <summary>
    /// Proactive PSIDTS rotation on the CookieRefreshIntervalMinutes cadence. Deliberately narrower
    /// than the reactive ladder: rung 1 only, and NO re-register (a successful rotation does not
    /// invalidate a live SIP registration, and re-registering every 8 min would re-create the
    /// 2026-06-19 REGISTER-storm risk — spec §4.1).
    /// </summary>
    private async Task RunProactiveCookieRefreshAsync()
    {
        try
        {
            if (!IsAvailable || _cookieSet == null || !_config.EnableCookieRotation) return;

            // Never talk to Google during a 603/403 account cooldown.
            if (_sipTransport?.IsThrottled == true)
            {
                _logger.LogDebug("GVApi: proactive PSIDTS refresh skipped — throttle cooldown active");
                return;
            }

            // Share the reactive single-flight guard so a tick can never race a recovery.
            if (IsRecoveryInFlight)
            {
                _logger.LogDebug("GVApi: proactive PSIDTS refresh skipped — recovery already in flight");
                return;
            }

            if (await TryRotateCookiesAsync())
                _logger.LogInformation("GVApi: proactive PSIDTS refresh succeeded");
            else
                _logger.LogWarning(
                    "GVApi: proactive PSIDTS refresh did not rotate — reactive 401 recovery remains the backstop");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: proactive PSIDTS refresh error");
        }
    }

    private async Task RunHealthCheckAsync()
    {
        try
        {
            // Bail only when there is genuinely nothing to probe. The guard is kept (rather than
            // dropped as redundant) because ProbeHealthAsync would otherwise return false for an
            // adapter that was never activated — or was concurrently deactivated — and that false
            // reads as "Google rejected our cookies", flipping availability and firing the recovery
            // ladder for no reason. It now honours HealthProbeOverride, without which the watchdog
            // — the whole subject of F7 — is unreachable through its own test seam.
            if (_accountClient == null && HealthProbeOverride == null) return;

            var healthy = await ProbeHealthAsync();
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
            else if (_sipTransport?.IsThrottled == true)
            {
                // Cookies fine and SIP not registered, but we're in a 603/403 throttle cooldown.
                // Forcing a re-register here is the storm that caused the 2026-06-19 incident — it
                // re-arms the loop every health-check interval. Defer: the transport's reconnect
                // loop will re-register once the cooldown elapses.
                _logger.LogWarning(
                    "GVApi: watchdog — deferring re-register: throttle cooldown active until {Until:o} ({Reason})",
                    _sipTransport.ThrottledUntil, _sipTransport.ThrottleReason);
            }
            else if (!IsRecoveryInFlight)
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
        _cookieRefreshTimer?.Dispose();
        _httpClient?.Dispose();
        // Same leak class as F6: these were never released here. The transport's DisposeAsync is
        // fire-and-forget because Dispose() must not block on async work.
        _rotatorHttpClient?.Dispose();
        if (_sipTransport is { } transport)
        {
            transport.IncomingCallReceived -= HandleSipIncomingCall;
            transport.AuthenticationFailed -= HandleAuthenticationFailed;
            _ = transport.DisposeAsync().AsTask();
        }
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
