using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using NAudio.Wave;
using System.Net;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Actual RTP audio bridge implementation with G.711 PCMU codec support
/// Provides bidirectional audio streaming between HT801 and Bluetooth
/// </summary>
public class RtpAudioBridge : IRtpAudioBridge, IDisposable
{
    private readonly ILogger<RtpAudioBridge> _logger;
    private RTPSession? _rtpSession;
    private AudioRoute _currentRoute = AudioRoute.RotaryPhone;
    private bool _isBridging;
    private bool _disposed;
    private CancellationTokenSource? _bridgeCts;
    
    // Audio buffers and processors
    private BufferedWaveProvider? _rtpToLocalBuffer;
    private WaveOutEvent? _waveOut;
    private WaveInEvent? _waveIn;
    
    // G.711 codec
    private const int SAMPLE_RATE = 8000; // 8 kHz for G.711
    private const int BITS_PER_SAMPLE = 16;
    private const int CHANNELS = 1; // Mono

    public event Action<AudioRoute>? OnAudioRouteChanged;
    public event Action? OnBridgeEstablished;
    public event Action? OnBridgeTerminated;
    public event Action<string>? OnBridgeError;

    public bool IsActive => _isBridging;
    public AudioRoute CurrentRoute => _currentRoute;

    public RtpAudioBridge(ILogger<RtpAudioBridge> logger)
    {
        _logger = logger;
        _logger.LogInformation("RtpAudioBridge initialized");
    }

    public async Task<bool> StartBridgeAsync(string rtpEndpoint, AudioRoute audioRoute)
    {
        try
        {
            _logger.LogInformation("Starting RTP audio bridge to {Endpoint} with route {Route}", 
                rtpEndpoint, audioRoute);

            if (_isBridging)
            {
                _logger.LogWarning("Bridge already running, stopping existing bridge first");
                await StopBridgeAsync();
            }

            _currentRoute = audioRoute;

            // Parse RTP endpoint string (format: "ip:port")
            var parts = rtpEndpoint.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            {
                throw new ArgumentException($"Invalid RTP endpoint format: {rtpEndpoint}. Expected format: 'ip:port'");
            }
            var rtpIpEndpoint = new IPEndPoint(IPAddress.Parse(parts[0]), port);

            // Create RTP session
            await CreateRtpSessionAsync(rtpIpEndpoint);

            // Set up audio processing based on route
            await SetupAudioProcessingAsync(audioRoute);

            _isBridging = true;
            OnBridgeEstablished?.Invoke();
            
            _logger.LogInformation("RTP audio bridge started successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start RTP audio bridge");
            OnBridgeError?.Invoke(ex.Message);
            return false;
        }
    }

    public async Task<bool> StopBridgeAsync()
    {
        try
        {
            _logger.LogInformation("Stopping RTP audio bridge");

            _bridgeCts?.Cancel();
            _bridgeCts?.Dispose();
            _bridgeCts = null;

            // Stop audio processing
            await StopAudioProcessingAsync();

            // Close RTP session
            _rtpSession?.Close("Bridge stopped");
            _rtpSession?.Dispose();
            _rtpSession = null;

            _isBridging = false;
            OnBridgeTerminated?.Invoke();
            
            _logger.LogInformation("RTP audio bridge stopped");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping RTP audio bridge");
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

            // Reconfigure audio processing for new route
            await StopAudioProcessingAsync();
            await SetupAudioProcessingAsync(newRoute);

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
        try
        {
            _logger.LogDebug("Creating RTP session for endpoint {Endpoint}", rtpEndpoint);

            // Create RTP session
            _rtpSession = new RTPSession(false, false, false);
            
            // Set up audio format (G.711 PCMU - payload type 0)
            var audioFormat = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000);
            
            // Add track
            var audioTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat> { audioFormat },
                MediaStreamStatusEnum.SendRecv);
            
            _rtpSession.addTrack(audioTrack);

            // Set remote endpoint
            _rtpSession.SetDestination(SDPMediaTypesEnum.audio, rtpEndpoint, rtpEndpoint);

            // Handle incoming RTP packets
            _rtpSession.OnRtpPacketReceived += HandleIncomingRtpPacket;

            // Start RTP session
            _rtpSession.Start();

            _logger.LogInformation("RTP session created and started");
            
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating RTP session");
            throw;
        }
    }

    private void HandleIncomingRtpPacket(IPEndPoint remoteEndpoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        try
        {
            if (mediaType != SDPMediaTypesEnum.audio)
                return;

            // Decode G.711 PCMU audio
            var pcmData = DecodeG711MuLaw(rtpPacket.Payload);

            // Route audio based on current route
            if (_currentRoute == AudioRoute.RotaryPhone)
            {
                // Send audio to rotary phone (already handled by RTP session to HT801)
                _logger.LogTrace("RTP audio routed to rotary phone");
            }
            else
            {
                // Send audio to Bluetooth device
                RouteAudioToBluetooth(pcmData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling incoming RTP packet");
        }
    }

    private async Task SetupAudioProcessingAsync(AudioRoute route)
    {
        try
        {
            _logger.LogDebug("Setting up audio processing for route: {Route}", route);

            _bridgeCts = new CancellationTokenSource();

            if (route == AudioRoute.RotaryPhone)
            {
                // Audio flows: RTP (from HT801) <-> Bluetooth
                // The HT801 handles the rotary phone audio directly
                // We just need to bridge RTP to Bluetooth for the mobile phone side
                await SetupRotaryPhoneAudioAsync();
            }
            else
            {
                // Audio flows: RTP (to HT801) <-> Bluetooth (active audio)
                // Capture from Bluetooth and send to RTP
                // Receive from RTP and send to Bluetooth
                await SetupCellPhoneAudioAsync();
            }

            _logger.LogInformation("Audio processing setup completed for {Route}", route);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up audio processing");
            throw;
        }
    }

    private async Task SetupRotaryPhoneAudioAsync()
    {
        try
        {
            _logger.LogDebug("Setting up rotary phone audio routing");
            
            // For rotary phone route:
            // - Incoming RTP from HT801 goes to Bluetooth (mobile phone hears rotary phone)
            // - Bluetooth audio is captured and sent via RTP to HT801 (rotary phone hears mobile)
            
            // Set up audio capture from system (Bluetooth will be the default input)
            SetupAudioCaptureAsync();
            
            _logger.LogInformation("Rotary phone audio routing configured");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up rotary phone audio");
            throw;
        }
    }

    private async Task SetupCellPhoneAudioAsync()
    {
        try
        {
            _logger.LogDebug("Setting up cell phone audio routing");
            
            // For cell phone route:
            // - Bluetooth audio is captured and sent via RTP to HT801
            // - RTP from HT801 is played to Bluetooth device
            
            // Set up audio playback to Bluetooth
            SetupAudioPlayback();
            
            // Set up audio capture from Bluetooth
            SetupAudioCaptureAsync();
            
            _logger.LogInformation("Cell phone audio routing configured");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up cell phone audio");
            throw;
        }
    }

    private void SetupAudioPlayback()
    {
        try
        {
            _logger.LogDebug("Setting up audio playback");
            
            // Create wave format for G.711 (8kHz, 16-bit, mono)
            var waveFormat = new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS);
            
            // Create buffer for incoming audio
            _rtpToLocalBuffer = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true
            };
            
            // Create wave output device
            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100 // 100ms latency
            };
            
            _waveOut.Init(_rtpToLocalBuffer);
            _waveOut.Play();
            
            _logger.LogInformation("Audio playback initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up audio playback");
            throw;
        }
    }

    private void SetupAudioCaptureAsync()
    {
        try
        {
            _logger.LogDebug("Setting up audio capture");
            
            // Create wave format for capture (8kHz, 16-bit, mono)
            var waveFormat = new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS);
            
            // Create wave input device
            _waveIn = new WaveInEvent
            {
                WaveFormat = waveFormat,
                BufferMilliseconds = 20 // 20ms buffers
            };
            
            _waveIn.DataAvailable += OnAudioCaptured;
            _waveIn.StartRecording();
            
            _logger.LogInformation("Audio capture initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up audio capture");
            throw;
        }
    }

    private void OnAudioCaptured(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (_rtpSession == null || !_isBridging)
                return;

            // Convert PCM to G.711 mu-law
            var muLawData = EncodeG711MuLaw(e.Buffer, e.BytesRecorded);

            // Send via RTP
            _rtpSession.SendAudio((uint)e.BytesRecorded, muLawData);
            
            _logger.LogTrace("Sent {Bytes} bytes of audio via RTP", muLawData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing captured audio");
        }
    }

    private void RouteAudioToBluetooth(byte[] pcmData)
    {
        try
        {
            if (_rtpToLocalBuffer != null)
            {
                _rtpToLocalBuffer.AddSamples(pcmData, 0, pcmData.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error routing audio to Bluetooth");
        }
    }

    private async Task StopAudioProcessingAsync()
    {
        try
        {
            _logger.LogDebug("Stopping audio processing");

            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            _rtpToLocalBuffer = null;

            _logger.LogInformation("Audio processing stopped");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping audio processing");
        }
    }

    // G.711 mu-law encoding/decoding
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

    private byte[] DecodeG711MuLaw(byte[] muLawData)
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

    private byte[] EncodeG711MuLaw(byte[] pcmData, int length)
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
        if (_disposed)
            return;

        _logger.LogInformation("Disposing RtpAudioBridge");
        
        StopBridgeAsync().Wait();
        
        _disposed = true;
    }
}
