#if !WINDOWS
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Linux RTP audio bridge using PipeWire (via pw-cat subprocess) for audio I/O.
/// Replaces NAudio's WaveInEvent/WaveOutEvent with PipeWire capture/playback streams.
///
/// Audio flow:
///   HT801 → RTP (SIPSorcery) → G.711 decode → pw-cat --playback → PipeWire sink
///   PipeWire source → pw-cat --record → G.711 encode → RTP → HT801
/// </summary>
public class PipeWireRtpAudioBridge : IRtpAudioBridge, IDisposable
{
  private readonly ILogger<PipeWireRtpAudioBridge> _logger;
  private RTPSession? _rtpSession;
  private AudioRoute _currentRoute = AudioRoute.RotaryPhone;
  private bool _isBridging;
  private bool _disposed;
  private CancellationTokenSource? _bridgeCts;

  // PipeWire subprocess handles
  private Process? _playbackProcess;
  private Process? _captureProcess;
  private Task? _captureReadTask;

  // G.711 codec constants
  private const int SAMPLE_RATE = 8000;
  private const int BITS_PER_SAMPLE = 16;
  private const int CHANNELS = 1;
  private const int BYTES_PER_SAMPLE = BITS_PER_SAMPLE / 8;

  public event Action<AudioRoute>? OnAudioRouteChanged;
  public event Action? OnBridgeEstablished;
  public event Action? OnBridgeTerminated;
  public event Action<string>? OnBridgeError;

  public bool IsActive => _isBridging;
  public AudioRoute CurrentRoute => _currentRoute;

  public PipeWireRtpAudioBridge(ILogger<PipeWireRtpAudioBridge> logger)
  {
    _logger = logger;
    _logger.LogInformation("PipeWireRtpAudioBridge initialized");
  }

  public async Task<bool> StartBridgeAsync(string rtpEndpoint, AudioRoute audioRoute)
  {
    try
    {
      _logger.LogInformation("Starting PipeWire RTP audio bridge to {Endpoint} with route {Route}",
        rtpEndpoint, audioRoute);

      if (_isBridging)
      {
        _logger.LogWarning("Bridge already running, stopping existing bridge first");
        await StopBridgeAsync();
      }

      _currentRoute = audioRoute;
      _bridgeCts = new CancellationTokenSource();

      // Parse RTP endpoint
      var parts = rtpEndpoint.Split(':');
      if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        throw new ArgumentException($"Invalid RTP endpoint format: {rtpEndpoint}. Expected: 'ip:port'");

      var rtpIpEndpoint = new IPEndPoint(IPAddress.Parse(parts[0]), port);

      // Create RTP session (SIPSorcery handles the protocol)
      await CreateRtpSessionAsync(rtpIpEndpoint);

      // Start PipeWire audio streams
      StartPlaybackStream();
      StartCaptureStream();

      _isBridging = true;
      OnBridgeEstablished?.Invoke();

      _logger.LogInformation("PipeWire RTP audio bridge started successfully");
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to start PipeWire RTP audio bridge");
      OnBridgeError?.Invoke(ex.Message);
      return false;
    }
  }

  public async Task<bool> StopBridgeAsync()
  {
    try
    {
      _logger.LogInformation("Stopping PipeWire RTP audio bridge");

      _bridgeCts?.Cancel();
      _bridgeCts?.Dispose();
      _bridgeCts = null;

      StopPipeWireStreams();

      _rtpSession?.Close("Bridge stopped");
      _rtpSession?.Dispose();
      _rtpSession = null;

      _isBridging = false;
      OnBridgeTerminated?.Invoke();

      _logger.LogInformation("PipeWire RTP audio bridge stopped");
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error stopping PipeWire RTP audio bridge");
      return false;
    }
  }

  public async Task<bool> ChangeAudioRouteAsync(AudioRoute newRoute)
  {
    try
    {
      _logger.LogInformation("Changing audio route from {OldRoute} to {NewRoute}",
        _currentRoute, newRoute);

      if (!_isBridging)
      {
        _logger.LogWarning("Cannot change audio route - bridge not running");
        return false;
      }

      _currentRoute = newRoute;

      // Restart PipeWire streams with new routing
      StopPipeWireStreams();
      StartPlaybackStream();
      StartCaptureStream();

      OnAudioRouteChanged?.Invoke(newRoute);

      _logger.LogInformation("Audio route changed successfully");
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error changing audio route");
      OnBridgeError?.Invoke(ex.Message);
      return false;
    }
  }

  private async Task CreateRtpSessionAsync(IPEndPoint rtpEndpoint)
  {
    _logger.LogDebug("Creating RTP session for endpoint {Endpoint}", rtpEndpoint);

    _rtpSession = new RTPSession(false, false, false);

    var audioFormat = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000);
    var audioTrack = new MediaStreamTrack(
      SDPMediaTypesEnum.audio,
      false,
      new List<SDPAudioVideoMediaFormat> { audioFormat },
      MediaStreamStatusEnum.SendRecv);

    _rtpSession.addTrack(audioTrack);
    _rtpSession.SetDestination(SDPMediaTypesEnum.audio, rtpEndpoint, rtpEndpoint);
    _rtpSession.OnRtpPacketReceived += HandleIncomingRtpPacket;

    await _rtpSession.Start();

    _logger.LogInformation("RTP session created and started");
  }

  private void HandleIncomingRtpPacket(IPEndPoint remoteEndpoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
  {
    try
    {
      if (mediaType != SDPMediaTypesEnum.audio || _playbackProcess?.HasExited != false)
        return;

      // Decode G.711 PCMU to PCM
      var pcmData = DecodeG711MuLaw(rtpPacket.Payload);

      // Write PCM data to pw-cat playback stdin
      try
      {
        _playbackProcess.StandardInput.BaseStream.Write(pcmData, 0, pcmData.Length);
        _playbackProcess.StandardInput.BaseStream.Flush();
      }
      catch (Exception ex)
      {
        _logger.LogDebug(ex, "Failed to write to playback stream");
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error handling incoming RTP packet");
    }
  }

  /// <summary>
  /// Starts pw-cat in playback mode — reads raw PCM from stdin and plays to PipeWire sink.
  /// </summary>
  private void StartPlaybackStream()
  {
    try
    {
      // pw-cat --playback - : reads raw audio from stdin
      // --format s16 : 16-bit signed integer
      // --rate 8000 : G.711 sample rate
      // --channels 1 : mono
      _playbackProcess = Process.Start(new ProcessStartInfo
      {
        FileName = "pw-cat",
        Arguments = "--playback - --format s16 --rate 8000 --channels 1",
        RedirectStandardInput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      });

      if (_playbackProcess == null)
      {
        _logger.LogError("Failed to start pw-cat playback process");
        return;
      }

      // Log stderr asynchronously
      _ = Task.Run(async () =>
      {
        try
        {
          while (!_playbackProcess.HasExited)
          {
            var line = await _playbackProcess.StandardError.ReadLineAsync();
            if (line != null)
              _logger.LogDebug("pw-cat playback: {Line}", line);
          }
        }
        catch { /* process exited */ }
      });

      _logger.LogInformation("PipeWire playback stream started (pid={Pid})", _playbackProcess.Id);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to start PipeWire playback stream");
    }
  }

  /// <summary>
  /// Starts pw-cat in record mode — captures audio from PipeWire source and sends via RTP.
  /// </summary>
  private void StartCaptureStream()
  {
    try
    {
      // pw-cat --record - : writes raw audio to stdout
      _captureProcess = Process.Start(new ProcessStartInfo
      {
        FileName = "pw-cat",
        Arguments = "--record - --format s16 --rate 8000 --channels 1",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      });

      if (_captureProcess == null)
      {
        _logger.LogError("Failed to start pw-cat capture process");
        return;
      }

      _logger.LogInformation("PipeWire capture stream started (pid={Pid})", _captureProcess.Id);

      // Read captured audio and send via RTP
      var ct = _bridgeCts?.Token ?? CancellationToken.None;
      _captureReadTask = Task.Run(async () =>
      {
        // 20ms frames at 8kHz mono 16-bit = 320 bytes
        var buffer = new byte[320];
        var stream = _captureProcess.StandardOutput.BaseStream;

        while (!ct.IsCancellationRequested && !_captureProcess.HasExited)
        {
          try
          {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
              int read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, ct);
              if (read == 0) break; // EOF
              totalRead += read;
            }

            if (totalRead == 0) break;

            // Encode PCM to G.711 and send via RTP
            if (_rtpSession != null && _isBridging)
            {
              var muLawData = EncodeG711MuLaw(buffer, totalRead);
              _rtpSession.SendAudio((uint)totalRead, muLawData);
            }
          }
          catch (OperationCanceledException)
          {
            break;
          }
          catch (Exception ex)
          {
            _logger.LogDebug(ex, "Error reading from capture stream");
            break;
          }
        }
      }, ct);

      // Log stderr
      _ = Task.Run(async () =>
      {
        try
        {
          while (!_captureProcess.HasExited)
          {
            var line = await _captureProcess.StandardError.ReadLineAsync();
            if (line != null)
              _logger.LogDebug("pw-cat capture: {Line}", line);
          }
        }
        catch { /* process exited */ }
      });
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to start PipeWire capture stream");
    }
  }

  private void StopPipeWireStreams()
  {
    try
    {
      if (_playbackProcess != null && !_playbackProcess.HasExited)
      {
        try
        {
          _playbackProcess.StandardInput.Close();
          if (!_playbackProcess.WaitForExit(2000))
            _playbackProcess.Kill();
        }
        catch { /* already exited */ }
        _playbackProcess.Dispose();
        _playbackProcess = null;
      }

      if (_captureProcess != null && !_captureProcess.HasExited)
      {
        try
        {
          _captureProcess.Kill();
          _captureProcess.WaitForExit(2000);
        }
        catch { /* already exited */ }
        _captureProcess.Dispose();
        _captureProcess = null;
      }

      _captureReadTask = null;

      _logger.LogDebug("PipeWire streams stopped");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error stopping PipeWire streams");
    }
  }

  // G.711 mu-law codec

  private static readonly short[] MuLawDecompressTable = GenerateMuLawDecompressTable();

  private static short[] GenerateMuLawDecompressTable()
  {
    var table = new short[256];
    for (int i = 0; i < 256; i++)
    {
      int sign = (i & 0x80) != 0 ? -1 : 1;
      int exponent = (i >> 4) & 0x07;
      int mantissa = i & 0x0F;
      int step = 4 << (exponent + 1);
      int value = sign * ((0x21 << exponent) + step * mantissa + step / 2 - 4 * 33);
      table[i] = (short)value;
    }
    return table;
  }

  private static byte[] DecodeG711MuLaw(byte[] muLawData)
  {
    var pcmData = new byte[muLawData.Length * 2];
    for (int i = 0; i < muLawData.Length; i++)
    {
      short pcmValue = MuLawDecompressTable[muLawData[i]];
      pcmData[i * 2] = (byte)(pcmValue & 0xFF);
      pcmData[i * 2 + 1] = (byte)((pcmValue >> 8) & 0xFF);
    }
    return pcmData;
  }

  private static byte[] EncodeG711MuLaw(byte[] pcmData, int length)
  {
    var muLawData = new byte[length / 2];
    for (int i = 0; i < length / 2; i++)
    {
      short pcmValue = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
      muLawData[i] = LinearToMuLaw(pcmValue);
    }
    return muLawData;
  }

  private static byte LinearToMuLaw(short pcm)
  {
    const int cClip = 32635;
    const int cBias = 0x84;

    int sign = (pcm < 0) ? 0x80 : 0;
    if (sign != 0)
      pcm = (short)-pcm;
    if (pcm > cClip)
      pcm = cClip;
    pcm += cBias;

    int exponent = 7;
    for (int expMask = 0x4000; (pcm & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

    int mantissa = (pcm >> (exponent + 3)) & 0x0F;
    int muLaw = ~(sign | (exponent << 4) | mantissa);

    return (byte)muLaw;
  }

  public void Dispose()
  {
    if (_disposed) return;

    _logger.LogInformation("Disposing PipeWireRtpAudioBridge");
    StopBridgeAsync().Wait();
    _disposed = true;
  }
}
#endif
