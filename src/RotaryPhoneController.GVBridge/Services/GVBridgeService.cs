using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Models;
using Serilog;

namespace RotaryPhoneController.GVBridge.Services;

public class GVBridgeService : IHostedService
{
    private readonly GVBridgeConfig _config;
    private readonly ILogger _logger;
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private WebSocket? _extensionSocket;
    private readonly ConcurrentQueue<byte[]> _inboundAudioQueue = new();
    private DateTime _lastPong = DateTime.UtcNow;

    public bool IsExtensionConnected => _extensionSocket?.State == WebSocketState.Open;
    public string? ExtensionVersion { get; private set; }

    public event Action<bool>? OnConnectionChanged;
    public event Action<IncomingCallMessage>? OnIncomingCall;
    public event Action<CallAnsweredMessage>? OnCallAnswered;
    public event Action<CallEndedMessage>? OnCallEnded;
    public event Action<SmsReceivedMessage>? OnSmsReceived;
    public event Action<MissedCallMessage>? OnMissedCall;
    public event Action<DtmfReceivedMessage>? OnDtmfReceived;

    public ConcurrentQueue<byte[]> InboundAudioQueue => _inboundAudioQueue;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public GVBridgeService(IOptions<GVBridgeConfig> config, ILogger logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://{_config.WebSocketHost}:{_config.WebSocketPort}/");
        _httpListener.Start();
        _logger.Information("GVBridgeService: WebSocket server listening on ws://{Host}:{Port}",
            _config.WebSocketHost, _config.WebSocketPort);

        _ = AcceptLoopAsync(_cts.Token);
        _ = HeartbeatLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _extensionSocket?.Dispose();
        _httpListener?.Stop();
        _logger.Information("GVBridgeService stopped");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _httpListener!.GetContextAsync();
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                if (IsExtensionConnected)
                {
                    _logger.Warning("Rejecting additional WebSocket connection — already connected");
                    context.Response.StatusCode = 409;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                _extensionSocket = wsContext.WebSocket;
                _lastPong = DateTime.UtcNow;
                _logger.Information("Chrome extension connected");
                OnConnectionChanged?.Invoke(true);

                await HandleConnectionAsync(_extensionSocket, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Error(ex, "WebSocket accept error");
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task HandleConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleMessage(json);
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.Warning("WebSocket connection lost: {Message}", ex.Message);
        }
        catch (OperationCanceledException) { }
        finally
        {
            ExtensionVersion = null;
            _extensionSocket = null;
            OnConnectionChanged?.Invoke(false);
            _logger.Information("Chrome extension disconnected");
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            // Parse the type discriminator manually for robustness
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "connected":
                    var connected = JsonSerializer.Deserialize<ConnectedMessage>(json, _jsonOptions);
                    ExtensionVersion = connected?.Version;
                    _logger.Information("Extension connected, version: {Version}", ExtensionVersion);
                    break;
                case "incomingCall":
                    var incoming = JsonSerializer.Deserialize<IncomingCallMessage>(json, _jsonOptions);
                    if (incoming != null) OnIncomingCall?.Invoke(incoming);
                    break;
                case "callAnswered":
                    var answered = JsonSerializer.Deserialize<CallAnsweredMessage>(json, _jsonOptions);
                    if (answered != null) OnCallAnswered?.Invoke(answered);
                    break;
                case "callEnded":
                    var ended = JsonSerializer.Deserialize<CallEndedMessage>(json, _jsonOptions);
                    if (ended != null) OnCallEnded?.Invoke(ended);
                    break;
                case "smsReceived":
                    var sms = JsonSerializer.Deserialize<SmsReceivedMessage>(json, _jsonOptions);
                    if (sms != null) OnSmsReceived?.Invoke(sms);
                    break;
                case "missedCall":
                    var missed = JsonSerializer.Deserialize<MissedCallMessage>(json, _jsonOptions);
                    if (missed != null) OnMissedCall?.Invoke(missed);
                    break;
                case "dtmfReceived":
                    var dtmf = JsonSerializer.Deserialize<DtmfReceivedMessage>(json, _jsonOptions);
                    if (dtmf != null) OnDtmfReceived?.Invoke(dtmf);
                    break;
                case "audioFrame":
                    var audio = JsonSerializer.Deserialize<AudioFrameMessage>(json, _jsonOptions);
                    if (audio != null)
                    {
                        var pcm = Convert.FromBase64String(audio.Pcm);
                        _inboundAudioQueue.Enqueue(pcm);
                    }
                    break;
                case "pong":
                    _lastPong = DateTime.UtcNow;
                    break;
                default:
                    _logger.Warning("Unknown extension message type: {Type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error handling extension message");
        }
    }

    public async Task SendMessageAsync(ExtensionMessage message)
    {
        if (_extensionSocket?.State != WebSocketState.Open)
        {
            _logger.Warning("Cannot send message — extension not connected");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(message, message.GetType(), _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _extensionSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error sending message to extension");
        }
    }

    public async Task SendAudioFrameAsync(byte[] pcmData)
    {
        var msg = new AudioFrameMessage { Pcm = Convert.ToBase64String(pcmData) };
        await SendMessageAsync(msg);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10000, ct);

            if (!IsExtensionConnected) continue;

            // Check if pong was received within timeout
            if ((DateTime.UtcNow - _lastPong).TotalSeconds > 15)
            {
                _logger.Warning("Extension heartbeat timeout — disconnecting");
                _extensionSocket?.Abort();
                continue;
            }

            await SendMessageAsync(new PingMessage());
        }
    }
}
