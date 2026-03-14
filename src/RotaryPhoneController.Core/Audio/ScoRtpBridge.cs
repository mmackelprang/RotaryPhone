#if !WINDOWS
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Bridges SCO audio (via local UDP from bt_manager.py) to RTP (to/from HT801).
///
/// Audio path:
///   SCO → bt_manager.py → UDP:scoRecvPort → this bridge → G.711 encode → RTP → HT801
///   HT801 → RTP → this bridge → G.711 decode → UDP:scoSendPort → bt_manager.py → SCO
/// </summary>
public class ScoRtpBridge : IRtpAudioBridge
{
    private readonly ILogger<ScoRtpBridge> _logger;
    private readonly int _scoRecvPort;  // UDP port to receive PCM from bt_manager.py
    private readonly int _scoSendPort;  // UDP port to send PCM to bt_manager.py
    private UdpClient? _scoRecvClient;
    private UdpClient? _scoSendClient;
    private UdpClient? _rtpClient;
    private CancellationTokenSource? _cts;
    private IPEndPoint? _rtpRemoteEndpoint;

    public bool IsActive { get; private set; }
    public AudioRoute CurrentRoute { get; private set; }

    public event Action? OnBridgeEstablished;
    public event Action? OnBridgeTerminated;
    public event Action<string>? OnBridgeError;

    public ScoRtpBridge(ILogger<ScoRtpBridge> logger, int scoRecvPort = 49100, int scoSendPort = 49101)
    {
        _logger = logger;
        _scoRecvPort = scoRecvPort;
        _scoSendPort = scoSendPort;
    }

    public async Task<bool> StartBridgeAsync(string rtpEndpoint, AudioRoute route)
    {
        if (IsActive) return false;

        // Parse "ip:port" string to IPEndPoint (matches IRtpAudioBridge signature)
        var parts = rtpEndpoint.Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var port))
        {
            _logger.LogError("Invalid RTP endpoint format: {Endpoint}", rtpEndpoint);
            return false;
        }

        _rtpRemoteEndpoint = new IPEndPoint(ip, port);
        CurrentRoute = route;
        _cts = new CancellationTokenSource();

        // UDP for SCO PCM data from/to bt_manager.py
        _scoRecvClient = new UdpClient(_scoRecvPort);
        _scoSendClient = new UdpClient();

        // UDP for RTP to/from HT801
        _rtpClient = new UdpClient(0); // Ephemeral port for RTP

        IsActive = true;
        OnBridgeEstablished?.Invoke();
        _logger.LogInformation("SCO-RTP bridge started: SCO recv={ScoRecv} send={ScoSend} RTP={Rtp}",
            _scoRecvPort, _scoSendPort, _rtpRemoteEndpoint);

        // Start bidirectional bridge
        var ct = _cts.Token;
        _ = Task.Run(() => ScoToRtpLoop(ct), ct);
        _ = Task.Run(() => RtpToScoLoop(ct), ct);

        return true;
    }

    private async Task ScoToRtpLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsActive)
            {
                var result = await _scoRecvClient!.ReceiveAsync(ct);
                var pcm = result.Buffer;
                if (pcm.Length == 0) continue;

                var encoded = G711Codec.EncodeMuLaw(pcm, pcm.Length);
                await _rtpClient!.SendAsync(encoded, encoded.Length, _rtpRemoteEndpoint!);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SCO→RTP loop error");
            OnBridgeError?.Invoke(ex.Message);
        }
    }

    private async Task RtpToScoLoop(CancellationToken ct)
    {
        var scoTarget = new IPEndPoint(IPAddress.Loopback, _scoSendPort);
        try
        {
            while (!ct.IsCancellationRequested && IsActive)
            {
                var result = await _rtpClient!.ReceiveAsync(ct);
                var rtpData = result.Buffer;
                if (rtpData.Length == 0) continue;

                var decoded = G711Codec.DecodeMuLaw(rtpData);
                await _scoSendClient!.SendAsync(decoded, decoded.Length, scoTarget);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTP→SCO loop error");
            OnBridgeError?.Invoke(ex.Message);
        }
    }

    public Task<bool> StopBridgeAsync()
    {
        if (!IsActive) return Task.FromResult(false);

        _cts?.Cancel();
        _scoRecvClient?.Close();
        _scoSendClient?.Close();
        _rtpClient?.Close();
        IsActive = false;
        OnBridgeTerminated?.Invoke();
        _logger.LogInformation("SCO-RTP bridge stopped");
        return Task.FromResult(true);
    }

    public Task<bool> ChangeAudioRouteAsync(AudioRoute newRoute)
    {
        CurrentRoute = newRoute;
        return Task.FromResult(true);
    }
}
#endif
