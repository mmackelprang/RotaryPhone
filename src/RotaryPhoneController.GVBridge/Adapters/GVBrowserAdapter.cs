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

        // Incoming call detection from the Chrome extension — this is the only
        // event we trust from the browser. It triggers CallManager to send a
        // SIP INVITE to ring the rotary phone.
        _bridgeService.OnIncomingCall += msg =>
        {
            _activeCallId = msg.CallId;
            _logger.LogInformation("GVBrowser: incoming call from {From} (callId={CallId})", msg.From, msg.CallId);
            OnIncomingCall?.Invoke(msg.From);
        };

        // We intentionally DO NOT forward OnCallAnswered or OnCallEnded from the
        // Chrome extension to CallManager. The GV browser UI changes for reasons
        // unrelated to what happens on the rotary phone (e.g., GV auto-answers for
        // voicemail, the call panel disappears when the call connects). These events
        // race with the SIP events from the HT801 (200 OK, BYE, hook changes) and
        // cause premature call termination.
        //
        // Instead, the SIP adapter (which is always active) handles:
        //   - Answer: HT801 sends 200 OK when handset is lifted → CallManager.AnswerCall()
        //   - Hangup: HT801 sends BYE or on-hook NOTIFY → CallManager.HangUp()
        //
        // The audio bridge is started/stopped from those SIP-driven state transitions,
        // not from the browser extension.

        _bridgeService.OnCallAnswered += msg =>
        {
            _logger.LogDebug("GVBrowser: extension reported callAnswered (ignored — SIP is authoritative)");
        };

        _bridgeService.OnCallEnded += msg =>
        {
            _logger.LogDebug("GVBrowser: extension reported callEnded (ignored — SIP is authoritative)");
            _activeCallId = null;
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

    public async Task OnCallAnsweredOnRotaryPhoneAsync()
    {
        _logger.LogInformation("GVBrowser: rotary phone answered — answering GV call and starting audio bridge");

        // Click the Answer button on the GV web page via CDP (Chrome DevTools Protocol).
        // This is the most reliable path — it directly executes JS in the page context,
        // bypassing all the WebSocket/extension messaging issues.
        _ = Task.Run(async () =>
        {
            try
            {
                await ClickGvAnswerViaCdpAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CDP answer click failed");
            }
        });

        // Start the audio bridge (WebSocket PCM ↔ RTP G.711)
        await _audioBridge.StartAsync();
    }

    /// <summary>
    /// Click the Answer button on the GV page via Chrome DevTools Protocol.
    /// Connects to Chromium's CDP port (9224) and evaluates JS to find and click the button.
    /// </summary>
    private async Task ClickGvAnswerViaCdpAsync()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var tabsJson = await http.GetStringAsync("http://127.0.0.1:9224/json");
        var tabs = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(tabsJson);

        string? wsUrl = null;
        foreach (var tab in tabs!)
        {
            var url = tab.GetProperty("url").GetString() ?? "";
            var type = tab.GetProperty("type").GetString() ?? "";
            if (type == "page" && url.Contains("voice.google.com"))
            {
                wsUrl = tab.GetProperty("webSocketDebuggerUrl").GetString();
                break;
            }
        }

        if (wsUrl == null)
        {
            _logger.LogWarning("CDP: No voice.google.com tab found");
            return;
        }

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        var clickJs = @"(function() {
            var btns = document.querySelectorAll('button');
            for (var i = 0; i < btns.length; i++) {
                var label = (btns[i].getAttribute('aria-label') || btns[i].innerText || '').toLowerCase();
                if (/\banswer\b|\baccept\b/.test(label)) {
                    btns[i].click();
                    return 'clicked: ' + label;
                }
            }
            return 'no answer button found';
        })()";

        var msg = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Runtime.evaluate",
            @params = new { expression = clickJs }
        });

        var bytes = System.Text.Encoding.UTF8.GetBytes(msg);
        await ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        var response = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

        _logger.LogInformation("CDP answer click result: {Response}", response.Length > 200 ? response[..200] : response);

        await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    public Task OnCallHungUpAsync()
    {
        _logger.LogInformation("GVBrowser: stopping audio bridge (call hung up)");
        return _audioBridge.StopAsync();
    }
}
