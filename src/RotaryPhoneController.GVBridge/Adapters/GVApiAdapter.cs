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
    private GvCookieRotationService? _rotationService;
    private GvCookieSet? _cookieSet;
    private HttpClient? _httpClient;
    private GvAccountClient? _accountClient;
    private GvSipTransport? _sipTransport;
    private Timer? _healthCheckTimer;

    private string? _activeCallId;
    private bool _disposed;

    public CallAdapterMode Mode => CallAdapterMode.GVApi;
    public bool IsAvailable { get; private set; }

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

        // 1. Load cookies from encrypted store
        _cookieStore = new GvCookieStore(_config.CookieFilePath, _config.CookieEncryptionKey);
        _cookieSet = await _cookieStore.LoadAsync();

        if (_cookieSet == null || string.IsNullOrEmpty(_cookieSet.Sapisid))
        {
            _logger.LogWarning("GVApi: No valid cookies found at {Path} — adapter unavailable. " +
                "Run the cookie extraction tool to import cookies.", _config.CookieFilePath);
            SetAvailable(false);
            return;
        }

        // 2. Start cookie rotation service (refreshes PSIDTS every 5 min)
        _rotationService = new GvCookieRotationService(
            _cookieStore, _loggerFactory.CreateLogger<GvCookieRotationService>());
        _cookieSet = await _rotationService.StartAsync(ct) ?? _cookieSet;

        // 3. Create authenticated HttpClient (reads from rotation service's live set)
        var handler = new GvHttpClientHandler(() =>
            Task.FromResult(_rotationService.CurrentCookieSet ?? _cookieSet!));
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // 4. Create account client for health checks
        _accountClient = new GvAccountClient(
            _httpClient, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvAccountClient>());

        // 5. Health check to verify cookies work
        var healthy = await _accountClient.IsHealthyAsync(ct);
        if (!healthy)
        {
            _logger.LogWarning("GVApi: Initial health check failed — cookies may be expired");
            SetAvailable(false);
            return;
        }

        // 6. Create SIP transport for call signaling + DTLS-SRTP audio
        var httpClientFactory = new SingleHttpClientFactory(_httpClient);
        var credProvider = new GvSipCredentialProvider(
            httpClientFactory, _config,
            _loggerFactory.CreateLogger<GvSipCredentialProvider>());

        _sipTransport = new GvSipTransport(
            _loggerFactory.CreateLogger<GvSipTransport>(),
            () => credProvider.GetCredentialsAsync(),
            _loggerFactory);

        _sipTransport.IncomingCallReceived += HandleSipIncomingCall;

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

        // Stop cookie rotation
        _rotationService?.Stop();

        // Disconnect SIP transport
        if (_sipTransport != null)
        {
            _sipTransport.IncomingCallReceived -= HandleSipIncomingCall;
            _sipTransport = null;
        }

        // Dispose HttpClient
        _httpClient?.Dispose();
        _httpClient = null;

        _accountClient = null;
        _cookieSet = null;
        _cookieStore = null;
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
