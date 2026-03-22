using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Adapters;

public class GVBrowserAdapter : ICallAdapter
{
    private readonly GVBridgeService _bridgeService;
    private readonly GVAudioBridgeService _audioBridge;
    private readonly ILogger<GVBrowserAdapter> _logger;
    private string? _activeCallId;

    public CallAdapterMode Mode => CallAdapterMode.GVBrowser;
    public bool IsAvailable => _bridgeService.IsExtensionConnected;

    public event Action<bool>? OnAvailabilityChanged;
    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnswered;
    public event Action? OnCallEnded;
    public event Action<string>? OnDtmfReceived;

    public GVBrowserAdapter(GVBridgeService bridgeService, GVAudioBridgeService audioBridge, ILogger<GVBrowserAdapter> logger)
    {
        _bridgeService = bridgeService;
        _audioBridge = audioBridge;
        _logger = logger;
    }

    public Task ActivateAsync(CancellationToken ct = default)
    {
        _bridgeService.OnConnectionChanged += connected => OnAvailabilityChanged?.Invoke(connected);
        _bridgeService.OnIncomingCall += msg =>
        {
            _activeCallId = msg.CallId;
            OnIncomingCall?.Invoke(msg.From);
        };
        _bridgeService.OnCallAnswered += msg => {
            OnCallAnswered?.Invoke();
            Task.Run(() => _audioBridge.StartAsync());
        };
        _bridgeService.OnCallEnded += msg => {
            _activeCallId = null;
            OnCallEnded?.Invoke();
            Task.Run(() => _audioBridge.StopAsync());
        };
        _bridgeService.OnDtmfReceived += msg => OnDtmfReceived?.Invoke(msg.Digit);

        _logger.LogInformation("GVBrowserAdapter activated");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("GVBrowserAdapter deactivated");
        return Task.CompletedTask;
    }

    public async Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Chrome extension not connected");

        await _bridgeService.SendMessageAsync(new DialMessage { Number = e164Number });
        _activeCallId = $"gv-{Guid.NewGuid():N}";
        _logger.LogInformation("GVBrowser: dialing {Number}", e164Number);
        return _activeCallId;
    }

    public async Task AnswerCallAsync(CancellationToken ct = default)
    {
        await _bridgeService.SendMessageAsync(new AnswerMessage());
        _logger.LogInformation("GVBrowser: answering call {CallId}", _activeCallId);
    }

    public async Task HangUpAsync(CancellationToken ct = default)
    {
        await _bridgeService.SendMessageAsync(new HangupMessage());
        _activeCallId = null;
        _logger.LogInformation("GVBrowser: hanging up");
    }
}
