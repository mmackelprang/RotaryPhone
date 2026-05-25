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

    public CallAdapterMode Mode => CallAdapterMode.GVApi;
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Whether the SIP transport is currently registered with Google Voice.
    /// </summary>
    public bool IsSipRegistered => _sipTransport?.IsRegistered ?? false;

    /// <summary>
    /// Whether the last cookie health check passed (cookies are still accepted by Google).
    /// </summary>
    public bool AreCookiesValid => _areCookiesValid;

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
        Interlocked.Exchange(ref _activeCallId, null);

        SetAvailable(false);
        _logger.LogInformation("GVApiAdapter deactivated");
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
        _logger.LogInformation("Rotary phone answered — starting audio bridge");

        if (_audioBridge != null && _sipTransport != null && _activeCallId != null)
        {
            _audioBridge.SetSipTransport(_sipTransport, _activeCallId);
            await _audioBridge.StartAsync();
        }
    }

    public async Task OnCallHungUpAsync()
    {
        _logger.LogInformation("Call hung up — stopping audio bridge");

        if (_audioBridge != null)
            await _audioBridge.StopAsync();

        var callId = Interlocked.Exchange(ref _activeCallId, null);
        if (callId != null && _sipTransport != null)
        {
            try { await _sipTransport.HangupAsync(callId); }
            catch (Exception ex) { _logger.LogWarning(ex, "SIP BYE on hangup failed"); }
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
