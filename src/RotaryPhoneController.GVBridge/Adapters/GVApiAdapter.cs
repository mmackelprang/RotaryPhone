using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Signaler;

namespace RotaryPhoneController.GVBridge.Adapters;

/// <summary>
/// ICallAdapter implementation that uses the Google Voice HTTP API
/// (authenticated via SAPISIDHASH cookies) instead of CDP browser automation.
/// </summary>
public class GVApiAdapter : ICallAdapter, IDisposable
{
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GVApiAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Set via SetServices() to avoid circular DI
    private GVBridgeService? _bridgeService;
    private GVAudioBridgeService? _audioBridge;

    // Internal components created during ActivateAsync
    private GvCookieStore? _cookieStore;
    private GvCookieRotationService? _rotationService;
    private GvCookieSet? _cookieSet;
    private HttpClient? _httpClient;
    private GvAccountClient? _accountClient;
    private GvCallClient? _callClient;
    private GvSmsClient? _smsClient;
    private GvSignalerClient? _signalerClient;
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
    /// Inject services that may cause circular DI if passed via constructor.
    /// Called after construction by the DI wiring layer.
    /// </summary>
    public void SetServices(GVBridgeService bridgeService, GVAudioBridgeService audioBridge)
    {
        _bridgeService = bridgeService;
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

        // 4. Create API clients with typed loggers
        _accountClient = new GvAccountClient(
            _httpClient, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvAccountClient>());

        _callClient = new GvCallClient(
            _httpClient, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvCallClient>());

        _smsClient = new GvSmsClient(
            _httpClient, _config.GvApiBaseUrl, _config.GvApiKey,
            _loggerFactory.CreateLogger<GvSmsClient>());

        // 5. Health check to verify cookies work
        var healthy = await _accountClient.IsHealthyAsync(ct);
        if (!healthy)
        {
            _logger.LogWarning("GVApi: Initial health check failed — cookies may be expired");
            SetAvailable(false);
            return;
        }

        // 6. Connect signaler for incoming call notifications
        _signalerClient = new GvSignalerClient(
            _httpClient, _config.SignalerBaseUrl,
            _loggerFactory.CreateLogger<GvSignalerClient>());

        _signalerClient.OnIncomingCall += evt =>
        {
            Interlocked.Exchange(ref _activeCallId, evt.CallId);
            _logger.LogInformation("GVApi: incoming call from {From} (callId={CallId})",
                evt.CallerNumber, evt.CallId);
            OnIncomingCall?.Invoke(evt.CallerNumber);
        };

        _signalerClient.OnCallEnded += evt =>
        {
            _logger.LogDebug("GVApi: signaler reported callEnded (ignored — SIP is authoritative)");
            Interlocked.Exchange(ref _activeCallId, null);
        };

        try
        {
            await _signalerClient.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: Failed to connect signaler — will retry on next health check");
        }

        // 7. Start periodic health check timer
        var intervalMs = _config.CookieHealthCheckIntervalMinutes * 60 * 1000;
        _healthCheckTimer = new Timer(OnHealthCheckTimer, null, intervalMs, intervalMs);

        SetAvailable(true);
        _logger.LogInformation("GVApiAdapter activated — API ready");
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

        // Disconnect signaler
        if (_signalerClient != null)
        {
            await _signalerClient.DisconnectAsync();
            _signalerClient = null;
        }

        // Dispose HttpClient
        _httpClient?.Dispose();
        _httpClient = null;

        _accountClient = null;
        _callClient = null;
        _smsClient = null;
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

        var result = await _callClient!.InitiateAsync(e164Number, ct);
        if (result == null)
            throw new InvalidOperationException($"Failed to initiate call to {e164Number}");

        // TODO: Extract real call ID from Google's protobuf-JSON response once format is confirmed via live testing
        var callId = $"gv-{Guid.NewGuid():N}";
        Interlocked.Exchange(ref _activeCallId, callId);
        _logger.LogInformation("Placed call {CallId} to {Number}", callId, e164Number);
        result.Dispose();
        return callId;
    }

    public Task AnswerCallAsync(CancellationToken ct = default)
    {
        // No-op: answering is SIP-driven. The actual answer happens in
        // OnCallAnsweredOnRotaryPhoneAsync when the handset is lifted.
        _logger.LogDebug("GVApi: AnswerCallAsync called (no-op — SIP is authoritative)");
        return Task.CompletedTask;
    }

    public async Task HangUpAsync(CancellationToken ct = default)
    {
        var callId = Interlocked.Exchange(ref _activeCallId, null);
        if (_callClient != null && callId != null)
        {
            try
            {
                await _callClient.HangupAsync(callId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "API hangup failed for call {CallId}", callId);
            }
        }
        _logger.LogInformation("GVApi: hanging up");
    }

    public async Task OnCallAnsweredOnRotaryPhoneAsync()
    {
        _logger.LogInformation("GVApi: rotary phone answered");

        if (_bridgeService != null)
        {
            try
            {
                await _bridgeService.SendMessageAsync(new AnswerMessage());
                await _bridgeService.SendMessageAsync(new MuteTabMessage());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send answer/mute to extension");
            }
        }

        if (_audioBridge != null)
            await _audioBridge.StartAsync();
    }

    public async Task OnCallHungUpAsync()
    {
        _logger.LogInformation("GVApi: stopping audio bridge and ending call");

        // Stop the audio bridge
        if (_audioBridge != null)
        {
            await _audioBridge.StopAsync();
        }

        // Tell the extension to hangup and unmute
        if (_bridgeService != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _bridgeService.SendMessageAsync(new HangupMessage());
                    await _bridgeService.SendMessageAsync(new UnmuteTabMessage());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send hangup/unmute to extension");
                }
            });
        }

        // Also hangup via API in case the extension is disconnected
        var callId = Interlocked.Exchange(ref _activeCallId, null);
        if (_callClient != null && callId != null)
        {
            try
            {
                await _callClient.HangupAsync(callId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "API hangup failed for call {CallId}", callId);
            }
        }
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

                // Try to reconnect signaler on next successful health check
                if (_signalerClient is { IsConnected: false })
                {
                    _logger.LogInformation("GVApi: signaler disconnected, will retry on next health check");
                }
            }
            else if (!IsAvailable)
            {
                _logger.LogInformation("GVApi: health check recovered — marking available");
                SetAvailable(true);

                // Reconnect signaler if needed
                if (_signalerClient is { IsConnected: false })
                {
                    try
                    {
                        await _signalerClient.ConnectAsync();
                        _logger.LogInformation("GVApi: signaler reconnected");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "GVApi: signaler reconnect failed");
                    }
                }
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
}
