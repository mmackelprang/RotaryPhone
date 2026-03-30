using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Audio;
using RotaryPhoneController.GVBridge.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Bridges audio between the GVBridgeService WebSocket (16 kHz PCM) and
/// the HT801 ATA (8 kHz G.711 µ-law over RTP).
/// </summary>
public class GVAudioBridgeService : IDisposable
{
    private readonly GVBridgeService _bridgeService;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GVAudioBridgeService> _logger;

    private RTPSession? _rtpSession;
    private CancellationTokenSource? _cts;
    private Task? _inboundLoopTask;

    /// <summary>
    /// Indicates whether the audio bridge is currently active (RTP session open and loop running).
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Running diagnostics counters.
    /// </summary>
    public AudioBridgeStats Stats { get; } = new();

    public GVAudioBridgeService(
        GVBridgeService bridgeService,
        IOptions<GVBridgeConfig> config,
        ILogger<GVAudioBridgeService> logger)
    {
        _bridgeService = bridgeService;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Start the RTP session and begin draining the inbound audio queue.
    /// </summary>
    public async Task StartAsync()
    {
        if (IsActive)
        {
            _logger.LogDebug("GVAudioBridge StartAsync called but already active — no-op");
            return;
        }

        _cts = new CancellationTokenSource();

        // Create RTP session bound to the configured local port (0 = OS-assigned).
        _rtpSession = new RTPSession(
            isMediaMultiplexed: false,
            isRtcpMultiplexed: false,
            isSecure: false,
            bindAddress: IPAddress.Parse(_config.LocalIp),
            bindPort: _config.LocalRtpPort);

        // Add a PCMU (G.711 µ-law) audio track.
        var pcmuTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio,
            false,
            new List<SDPAudioVideoMediaFormat> { new(SDPWellKnownMediaFormatsEnum.PCMU) });
        _rtpSession.addTrack(pcmuTrack);

        // Set the remote RTP destination (HT801 ATA).
        var remoteRtpEP = new IPEndPoint(IPAddress.Parse(_config.HT801Ip), _config.HT801RtpPort);
        var remoteRtcpEP = new IPEndPoint(IPAddress.Parse(_config.HT801Ip), _config.HT801RtpPort + 1);
        _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteRtpEP, remoteRtcpEP);

        // Accept RTP from any source (the HT801 may use a different port for sending).
        _rtpSession.AcceptRtpFromAny = true;

        // Subscribe to inbound RTP packets from the HT801.
        _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

        // Start the RTP session (begins RTCP reporting).
        await _rtpSession.Start();

        // Start the inbound audio loop (WebSocket PCM -> RTP G.711).
        _inboundLoopTask = InboundLoopAsync(_cts.Token);

        IsActive = true;
        _logger.LogInformation(
            "GVAudioBridge started — local RTP port {Port}, destination {Dest}:{DestPort}",
            _config.LocalRtpPort, _config.HT801Ip, _config.HT801RtpPort);
    }

    /// <summary>
    /// Stop the inbound loop and close the RTP session.
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsActive)
        {
            _logger.LogDebug("GVAudioBridge StopAsync called but not active — no-op");
            return;
        }

        IsActive = false;

        // Cancel the inbound loop.
        _cts?.Cancel();
        if (_inboundLoopTask != null)
        {
            try { await _inboundLoopTask; }
            catch (OperationCanceledException) { }
        }

        // Unsubscribe and close the RTP session.
        if (_rtpSession != null)
        {
            _rtpSession.OnRtpPacketReceived -= OnRtpPacketReceived;
            _rtpSession.Close("GVAudioBridge stopped");
            _rtpSession.Dispose();
            _rtpSession = null;
        }

        _cts?.Dispose();
        _cts = null;
        _inboundLoopTask = null;

        _logger.LogInformation("GVAudioBridge stopped");
    }

    /// <summary>
    /// Drains the GVBridgeService inbound audio queue (16 kHz PCM from Chrome extension),
    /// resamples to 8 kHz, encodes to G.711 µ-law, and sends via RTP to the HT801.
    /// </summary>
    private async Task InboundLoopAsync(CancellationToken ct)
    {
        // At 8 kHz with 20ms frames, each RTP packet carries 160 samples = 160 bytes of G.711.
        // The RTP timestamp increment per frame is 160.
        const uint timestampIncrement = 160;

        while (!ct.IsCancellationRequested)
        {
            if (_bridgeService.InboundAudioQueue.TryDequeue(out var pcm16k))
            {
                try
                {
                    // Resample 16 kHz -> 8 kHz
                    var pcm8k = AudioResampler.Resample16kTo8k(pcm16k);

                    // Encode PCM 8 kHz (16-bit signed) -> G.711 µ-law
                    var mulaw = MuLawEncoder.Encode(pcm8k);

                    // Send via RTP
                    _rtpSession?.SendAudio(timestampIncrement, mulaw);

                    Stats.RecordInboundSent();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing inbound audio frame");
                    Stats.RecordInboundError();
                }
            }
            else
            {
                // No frames available; yield briefly to avoid busy-wait.
                try { await Task.Delay(1, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Handles inbound RTP packets from the HT801 (G.711 µ-law), decodes to PCM,
    /// resamples 8 kHz -> 16 kHz, and forwards to the Chrome extension via WebSocket.
    /// </summary>
    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio)
            return;

        try
        {
            var mulaw = rtpPacket.Payload;

            // Decode G.711 µ-law -> PCM 8 kHz (16-bit signed)
            var pcm8k = MuLawEncoder.Decode(mulaw);

            // Resample 8 kHz -> 16 kHz
            var pcm16k = AudioResampler.Resample8kTo16k(pcm8k);

            // Send to Chrome extension via WebSocket
            _ = _bridgeService.SendAudioFrameAsync(pcm16k);

            Stats.RecordOutboundReceived();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing outbound RTP packet");
            Stats.RecordOutboundError();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// G.711 µ-law encoder/decoder.
/// Converts between 16-bit signed linear PCM and 8-bit µ-law compressed samples.
/// </summary>
internal static class MuLawEncoder
{
    private const int MuLawBias = 0x84;   // 132, added before compression
    private const int MuLawClip = 32635;  // max magnitude before clipping

    /// <summary>
    /// Encode 16-bit signed linear PCM (little-endian byte pairs) to G.711 µ-law bytes.
    /// </summary>
    public static byte[] Encode(byte[] pcm)
    {
        int sampleCount = pcm.Length / 2;
        var mulaw = new byte[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            mulaw[i] = EncodeSample(sample);
        }
        return mulaw;
    }

    /// <summary>
    /// Decode G.711 µ-law bytes to 16-bit signed linear PCM (little-endian byte pairs).
    /// </summary>
    public static byte[] Decode(byte[] mulaw)
    {
        var pcm = new byte[mulaw.Length * 2];
        for (int i = 0; i < mulaw.Length; i++)
        {
            short sample = DecodeSample(mulaw[i]);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    /// <summary>
    /// Encode a single 16-bit signed sample to µ-law.
    /// ITU-T G.711 standard algorithm.
    /// </summary>
    private static byte EncodeSample(short sample)
    {
        // Determine sign
        int sign = (sample >> 8) & 0x80;
        if (sign != 0)
            sample = (short)-sample;

        // Clip magnitude
        if (sample > MuLawClip)
            sample = MuLawClip;

        // Add bias
        sample = (short)(sample + MuLawBias);

        // Find the segment (exponent)
        int exponent = 7;
        int mask = 0x4000;
        for (; exponent > 0; exponent--)
        {
            if ((sample & mask) != 0)
                break;
            mask >>= 1;
        }

        // Extract mantissa (4 bits)
        int mantissa = (sample >> (exponent + 3)) & 0x0F;

        // Combine sign, exponent, mantissa and complement
        byte mulawByte = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)~mulawByte;
    }

    /// <summary>
    /// Decode a single µ-law byte to a 16-bit signed sample.
    /// ITU-T G.711 standard algorithm.
    /// </summary>
    private static short DecodeSample(byte mulaw)
    {
        mulaw = (byte)~mulaw;
        int sign = mulaw & 0x80;
        int exponent = (mulaw >> 4) & 0x07;
        int mantissa = mulaw & 0x0F;

        // Reconstruct the magnitude per ITU-T G.711:
        // mantissa << 3 restores the 4 mantissa bits to their original position,
        // MuLawBias adds back the bias applied during encoding.
        int sample = ((mantissa << 3) + MuLawBias) << exponent;

        if (sign != 0)
            sample = -sample;

        return (short)sample;
    }
}

/// <summary>
/// Diagnostics counters for the audio bridge.
/// </summary>
public class AudioBridgeStats
{
    private long _inboundFramesSent;
    private long _outboundFramesReceived;
    private long _inboundErrors;
    private long _outboundErrors;

    /// <summary>Number of PCM frames from WebSocket successfully encoded and sent as RTP.</summary>
    public long InboundFramesSent => Interlocked.Read(ref _inboundFramesSent);

    /// <summary>Number of RTP frames received from HT801 and forwarded to WebSocket.</summary>
    public long OutboundFramesReceived => Interlocked.Read(ref _outboundFramesReceived);

    /// <summary>Number of errors during inbound (WebSocket -> RTP) processing.</summary>
    public long InboundErrors => Interlocked.Read(ref _inboundErrors);

    /// <summary>Number of errors during outbound (RTP -> WebSocket) processing.</summary>
    public long OutboundErrors => Interlocked.Read(ref _outboundErrors);

    public void RecordInboundSent() => Interlocked.Increment(ref _inboundFramesSent);
    public void RecordOutboundReceived() => Interlocked.Increment(ref _outboundFramesReceived);
    public void RecordInboundError() => Interlocked.Increment(ref _inboundErrors);
    public void RecordOutboundError() => Interlocked.Increment(ref _outboundErrors);
}
