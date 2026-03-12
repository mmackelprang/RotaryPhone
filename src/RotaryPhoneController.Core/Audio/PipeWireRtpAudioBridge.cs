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

      await StopPipeWireStreamsAsync();

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
      await StopPipeWireStreamsAsync();
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

      var pcmData = G711Codec.DecodeMuLaw(rtpPacket.Payload);

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

  private void StartPlaybackStream()
  {
    try
    {
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

      var ct = _bridgeCts?.Token ?? CancellationToken.None;
      _ = Task.Run(async () =>
      {
        try
        {
          while (!_playbackProcess.HasExited && !ct.IsCancellationRequested)
          {
            var line = await _playbackProcess.StandardError.ReadLineAsync(ct);
            if (line != null)
              _logger.LogDebug("pw-cat playback: {Line}", line);
          }
        }
        catch { /* process exited or cancelled */ }
      }, ct);

      _logger.LogInformation("PipeWire playback stream started (pid={Pid})", _playbackProcess.Id);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to start PipeWire playback stream");
    }
  }

  private void StartCaptureStream()
  {
    try
    {
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
              if (read == 0) break;
              totalRead += read;
            }

            if (totalRead == 0) break;

            if (_rtpSession != null && _isBridging)
            {
              var muLawData = G711Codec.EncodeMuLaw(buffer, totalRead);
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

      // Log stderr with cancellation support
      _ = Task.Run(async () =>
      {
        try
        {
          while (!_captureProcess.HasExited && !ct.IsCancellationRequested)
          {
            var line = await _captureProcess.StandardError.ReadLineAsync(ct);
            if (line != null)
              _logger.LogDebug("pw-cat capture: {Line}", line);
          }
        }
        catch { /* process exited or cancelled */ }
      }, ct);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to start PipeWire capture stream");
    }
  }

  private async Task StopPipeWireStreamsAsync()
  {
    try
    {
      // Await the capture read task with timeout before killing processes
      if (_captureReadTask != null)
      {
        try
        {
          await _captureReadTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* timeout or cancellation */ }
        _captureReadTask = null;
      }

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

      _logger.LogDebug("PipeWire streams stopped");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error stopping PipeWire streams");
    }
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
