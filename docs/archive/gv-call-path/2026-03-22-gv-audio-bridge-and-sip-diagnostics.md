# GV Audio Bridge & SIP Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable end-to-end Google Voice calls through the rotary phone (audio bridge) and provide real-time SIP/HT801 diagnostics for debugging connectivity issues.

**Architecture:** Two workstreams. (1) GVAudioBridgeService transcodes between WebSocket PCM frames (16kHz from Chrome extension) and G.711 µ-law RTP (8kHz to HT801 ATA). (2) SipDiagnosticService instruments all SIP messages, tracks HT801 health, and generates diagnostic annotations for INVITE failures — exposed via SignalR and REST API, consumed by a React diagnostics page.

**Tech Stack:** .NET 10 / C# / SIPSorcery (RTP) / xUnit+Moq (tests) / React+MUI+Vite (frontend) / SignalR / Chrome Extension Manifest V3

**Spec:** `docs/superpowers/specs/2026-03-22-gv-audio-bridge-and-sip-diagnostics-design.md`

---

## File Structure

### New Files

| File | Purpose |
|------|---------|
| `src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs` | 16kHz↔8kHz sample rate conversion |
| `src/RotaryPhoneController.GVBridge/Services/GVAudioBridgeService.cs` | Core audio bridge: WebSocket PCM ↔ RTP G.711 |
| `src/RotaryPhoneController.Core/Diagnostics/SipMessageEntry.cs` | SIP message log data model + enums |
| `src/RotaryPhoneController.Core/Diagnostics/CallTimelineEntry.cs` | Call timeline event model |
| `src/RotaryPhoneController.Core/Diagnostics/Ht801HealthStatus.cs` | HT801 health snapshot model |
| `src/RotaryPhoneController.Core/Diagnostics/ConfigParameter.cs` | HT801 config comparison model |
| `src/RotaryPhoneController.Core/Diagnostics/SipDiagnosticService.cs` | SIP event aggregation, diagnostics, HT801 health |
| `src/RotaryPhoneController.Server/Controllers/DiagnosticsController.cs` | REST endpoints for diagnostics |
| `src/RotaryPhoneController.Client/src/pages/Diagnostics.tsx` | Main diagnostics page |
| `src/RotaryPhoneController.Client/src/components/diagnostics/StatusBar.tsx` | Top status cards |
| `src/RotaryPhoneController.Client/src/components/diagnostics/SipMessageLog.tsx` | SIP message log panel |
| `src/RotaryPhoneController.Client/src/components/diagnostics/Ht801HealthPanel.tsx` | HT801 health + config validation |
| `src/RotaryPhoneController.Client/src/components/diagnostics/RtpStatsPanel.tsx` | RTP audio stats panel |
| `src/RotaryPhoneController.Client/src/components/diagnostics/CallTimeline.tsx` | Call state timeline |
| `src/RotaryPhoneController.Client/src/hooks/useDiagnostics.ts` | SignalR hook for diagnostics events |
| `ChromeExtension/offscreen/audio-bridge.js` | tabCapture→PCM capture and PCM→playback |

### Modified Files

| File | Changes |
|------|---------|
| `src/RotaryPhoneController.GVBridge/Adapters/GVBrowserAdapter.cs` | Add GVAudioBridgeService to constructor, chain audio start/stop in event lambdas |
| `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs` | Register GVAudioBridgeService in DI |
| `src/RotaryPhoneController.Core/SIPSorceryAdapter.cs` | Add OnSipMessageLogged event, configurable SDP RTP port |
| `src/RotaryPhoneController.Server/Services/SignalRNotifierService.cs` | Subscribe to SipDiagnosticService, broadcast diagnostics |
| `src/RotaryPhoneController.Core/HT801/HT801ConfigService.cs` | Add CompareConfigAsync wrapper |
| `src/RotaryPhoneController.Server/Program.cs` | Register SipDiagnosticService |
| `src/RotaryPhoneController.Client/src/App.tsx` | Add /diagnostics route |
| `src/RotaryPhoneController.Client/src/services/api.ts` | Add diagnostics API functions |
| `ChromeExtension/offscreen/offscreen.html` | Add script tag for audio-bridge.js |
| `ChromeExtension/content/gv-bridge.js` | Relay audioFrame between WS and offscreen |
| `ChromeExtension/background/service-worker.js` | tabCapture stream ID + offscreen doc creation |

### Test Files

| File | Tests |
|------|-------|
| `src/RotaryPhoneController.GVBridge.Tests/AudioResamplerTests.cs` | Resample accuracy, edge cases |
| `src/RotaryPhoneController.GVBridge.Tests/GVAudioBridgeServiceTests.cs` | Start/stop lifecycle, frame flow |
| `src/RotaryPhoneController.Tests/SipDiagnosticServiceTests.cs` | Diagnostic annotations, ring buffer, health tracking |

---

## Task 1: AudioResampler

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs`
- Create: `src/RotaryPhoneController.GVBridge.Tests/AudioResamplerTests.cs`

- [ ] **Step 1: Write failing tests for AudioResampler**

Create `src/RotaryPhoneController.GVBridge.Tests/AudioResamplerTests.cs`:

```csharp
using RotaryPhoneController.GVBridge.Audio;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests;

public class AudioResamplerTests
{
    [Fact]
    public void Resample16kTo8k_HalvesSampleCount()
    {
        // 320 samples at 16kHz = 20ms → should produce 160 samples at 8kHz
        var input = new byte[640]; // 320 samples × 2 bytes
        // Fill with a simple ramp pattern
        for (int i = 0; i < 320; i++)
        {
            var value = (short)(i * 100);
            input[i * 2] = (byte)(value & 0xFF);
            input[i * 2 + 1] = (byte)(value >> 8);
        }

        var result = AudioResampler.Resample16kTo8k(input);

        Assert.Equal(320, result.Length); // 160 samples × 2 bytes
    }

    [Fact]
    public void Resample8kTo16k_DoublesSampleCount()
    {
        // 160 samples at 8kHz = 20ms → should produce 320 samples at 16kHz
        var input = new byte[320]; // 160 samples × 2 bytes
        for (int i = 0; i < 160; i++)
        {
            var value = (short)(i * 200);
            input[i * 2] = (byte)(value & 0xFF);
            input[i * 2 + 1] = (byte)(value >> 8);
        }

        var result = AudioResampler.Resample8kTo16k(input);

        Assert.Equal(640, result.Length); // 320 samples × 2 bytes
    }

    [Fact]
    public void Resample16kTo8k_PreservesSignalShape()
    {
        // A 1kHz sine at 16kHz should produce a 1kHz sine at 8kHz
        var input = new byte[640];
        for (int i = 0; i < 320; i++)
        {
            var value = (short)(short.MaxValue * Math.Sin(2 * Math.PI * 1000 * i / 16000.0));
            input[i * 2] = (byte)(value & 0xFF);
            input[i * 2 + 1] = (byte)(value >> 8);
        }

        var result = AudioResampler.Resample16kTo8k(input);

        // Verify output has non-trivial amplitude (signal wasn't destroyed)
        short maxAbs = 0;
        for (int i = 0; i < result.Length / 2; i++)
        {
            var sample = (short)(result[i * 2] | (result[i * 2 + 1] << 8));
            maxAbs = Math.Max(maxAbs, Math.Abs(sample));
        }
        Assert.True(maxAbs > short.MaxValue / 2, $"Signal amplitude too low: {maxAbs}");
    }

    [Fact]
    public void Resample_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(AudioResampler.Resample16kTo8k(Array.Empty<byte>()));
        Assert.Empty(AudioResampler.Resample8kTo16k(Array.Empty<byte>()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~AudioResampler" -v minimal`
Expected: Build error — `AudioResampler` type not found.

- [ ] **Step 3: Implement AudioResampler**

Create `src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Audio;

/// <summary>
/// Simple linear interpolation resampler for 16kHz ↔ 8kHz PCM (16-bit mono).
/// G.711 telephony audio doesn't benefit from higher-quality algorithms.
/// </summary>
public static class AudioResampler
{
    /// <summary>Downsample 16kHz PCM to 8kHz PCM (16-bit mono, little-endian).</summary>
    public static byte[] Resample16kTo8k(byte[] pcm16k)
    {
        if (pcm16k.Length == 0) return Array.Empty<byte>();

        int sampleCount = pcm16k.Length / 2;
        int outCount = sampleCount / 2;
        var result = new byte[outCount * 2];

        for (int i = 0; i < outCount; i++)
        {
            int srcIdx = i * 2;
            // Average two adjacent samples for basic anti-aliasing
            short s0 = (short)(pcm16k[srcIdx * 2] | (pcm16k[srcIdx * 2 + 1] << 8));
            short s1 = (short)(pcm16k[(srcIdx + 1) * 2] | (pcm16k[(srcIdx + 1) * 2 + 1] << 8));
            short avg = (short)((s0 + s1) / 2);
            result[i * 2] = (byte)(avg & 0xFF);
            result[i * 2 + 1] = (byte)(avg >> 8);
        }

        return result;
    }

    /// <summary>Upsample 8kHz PCM to 16kHz PCM (16-bit mono, little-endian).</summary>
    public static byte[] Resample8kTo16k(byte[] pcm8k)
    {
        if (pcm8k.Length == 0) return Array.Empty<byte>();

        int sampleCount = pcm8k.Length / 2;
        int outCount = sampleCount * 2;
        var result = new byte[outCount * 2];

        for (int i = 0; i < sampleCount; i++)
        {
            short current = (short)(pcm8k[i * 2] | (pcm8k[i * 2 + 1] << 8));
            short next = (i + 1 < sampleCount)
                ? (short)(pcm8k[(i + 1) * 2] | (pcm8k[(i + 1) * 2 + 1] << 8))
                : current;
            short interp = (short)((current + next) / 2);

            int outIdx = i * 2;
            // Original sample
            result[outIdx * 2] = (byte)(current & 0xFF);
            result[outIdx * 2 + 1] = (byte)(current >> 8);
            // Interpolated sample
            result[(outIdx + 1) * 2] = (byte)(interp & 0xFF);
            result[(outIdx + 1) * 2 + 1] = (byte)(interp >> 8);
        }

        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~AudioResampler" -v minimal`
Expected: All 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs src/RotaryPhoneController.GVBridge.Tests/AudioResamplerTests.cs
git commit -m "feat(gvbridge): add AudioResampler for 16kHz↔8kHz PCM conversion"
```

---

## Task 2: GVAudioBridgeService

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Services/GVAudioBridgeService.cs`
- Create: `src/RotaryPhoneController.GVBridge.Tests/GVAudioBridgeServiceTests.cs`

**References:**
- `src/RotaryPhoneController.GVBridge/Services/GVBridgeService.cs` — `InboundAudioQueue` (line 34), `SendAudioFrameAsync` (line 223)
- `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs` — `LocalRtpPort` (line 7), `HT801Ip` (line 10), `HT801RtpPort` (line 11)
- `src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs` — from Task 1

- [ ] **Step 1: Write failing tests for GVAudioBridgeService**

Create `src/RotaryPhoneController.GVBridge.Tests/GVAudioBridgeServiceTests.cs`.

Note: `GVBridgeService` is a concrete class with no interface, so it cannot be mocked with Moq. Tests use a real `GVBridgeService` instance (its `InboundAudioQueue` is a simple `ConcurrentQueue` that works without starting the service):

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests;

public class GVAudioBridgeServiceTests
{
    private readonly GVBridgeConfig _config;
    private readonly GVAudioBridgeService _service;

    public GVAudioBridgeServiceTests()
    {
        _config = new GVBridgeConfig
        {
            LocalRtpPort = 0, // Let OS assign port for tests
            HT801Ip = "127.0.0.1",
            HT801RtpPort = 15004
        };

        // GVBridgeService is concrete — construct directly (InboundAudioQueue works without Start)
        var bridgeService = new GVBridgeService(
            Options.Create(_config),
            new Serilog.Core.Logger(new Serilog.Core.LoggingLevelSwitch())
        );

        _service = new GVAudioBridgeService(
            bridgeService,
            Options.Create(_config),
            Mock.Of<ILogger<GVAudioBridgeService>>()
        );
    }

    [Fact]
    public void IsActive_FalseByDefault()
    {
        Assert.False(_service.IsActive);
    }

    [Fact]
    public async Task StartAsync_SetsIsActiveTrue()
    {
        await _service.StartAsync();
        Assert.True(_service.IsActive);
        await _service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_SetsIsActiveFalse()
    {
        await _service.StartAsync();
        await _service.StopAsync();
        Assert.False(_service.IsActive);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyActive_IsNoOp()
    {
        await _service.StartAsync();
        await _service.StartAsync(); // Should not throw
        Assert.True(_service.IsActive);
        await _service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhenNotActive_IsNoOp()
    {
        await _service.StopAsync(); // Should not throw
        Assert.False(_service.IsActive);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GVAudioBridgeService" -v minimal`
Expected: Build error — `GVAudioBridgeService` type not found.

- [ ] **Step 3: Implement GVAudioBridgeService**

Create `src/RotaryPhoneController.GVBridge/Services/GVAudioBridgeService.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Audio;
using RotaryPhoneController.GVBridge.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Bridges audio between the GV Chrome extension (WebSocket PCM) and the HT801 ATA (RTP G.711).
/// Owns a dedicated RTP session on GVBridgeConfig.LocalRtpPort.
/// </summary>
public class GVAudioBridgeService : IDisposable
{
    private readonly GVBridgeService _bridgeService;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GVAudioBridgeService> _logger;

    private RTPSession? _rtpSession;
    private CancellationTokenSource? _cts;
    private Task? _inboundTask;

    // Diagnostics
    private int _rtpPacketsSent;
    private int _rtpPacketsReceived;
    private int _wsFramesProcessed;
    public event Action<AudioBridgeStats>? OnStatsUpdate;

    public bool IsActive { get; private set; }

    public GVAudioBridgeService(
        GVBridgeService bridgeService,
        IOptions<GVBridgeConfig> config,
        ILogger<GVAudioBridgeService> logger)
    {
        _bridgeService = bridgeService;
        _config = config.Value;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        if (IsActive) return;

        _cts = new CancellationTokenSource();
        _rtpPacketsSent = 0;
        _rtpPacketsReceived = 0;
        _wsFramesProcessed = 0;

        // Create RTP session with G.711 PCMU codec, bound to configured local port
        var localEP = new IPEndPoint(IPAddress.Any, _config.LocalRtpPort);
        _rtpSession = new RTPSession(false, false, false, localEP);
        var pcmuFormat = new SDPAudioVideoMediaFormat(
            SDPWellKnownMediaFormatsEnum.PCMU);
        var track = new MediaStreamTrack(pcmuFormat);
        _rtpSession.addTrack(track);

        // Set remote endpoint (HT801)
        var remoteEP = new IPEndPoint(
            IPAddress.Parse(_config.HT801Ip),
            _config.HT801RtpPort);
        var rtcpEP = new IPEndPoint(IPAddress.Parse(_config.HT801Ip), _config.HT801RtpPort + 1);
        _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEP, rtcpEP);

        // Handle incoming RTP from HT801 (outbound audio: phone → GV caller)
        _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

        // Start the RTP session
        await _rtpSession.Start();

        // Start inbound loop (GV caller → phone)
        _inboundTask = Task.Run(() => InboundLoopAsync(_cts.Token));

        IsActive = true;
        _logger.LogInformation("GV audio bridge started on port {Port} → {Remote}",
            _config.LocalRtpPort, remoteEP);
    }

    public async Task StopAsync()
    {
        if (!IsActive) return;

        _cts?.Cancel();

        if (_inboundTask != null)
        {
            try { await _inboundTask; } catch (OperationCanceledException) { }
        }

        if (_rtpSession != null)
        {
            _rtpSession.OnRtpPacketReceived -= OnRtpPacketReceived;
            _rtpSession.Close("bridge stopped");
            _rtpSession = null;
        }

        IsActive = false;
        _logger.LogInformation("GV audio bridge stopped. Sent={Sent} Received={Received} WSFrames={WS}",
            _rtpPacketsSent, _rtpPacketsReceived, _wsFramesProcessed);
    }

    public void Dispose()
    {
        if (IsActive) StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>Drains InboundAudioQueue (GV caller PCM) → resample → G.711 → RTP to HT801.</summary>
    private async Task InboundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_bridgeService.InboundAudioQueue.TryDequeue(out var pcm16k))
            {
                try
                {
                    // Resample 16kHz → 8kHz
                    var pcm8k = AudioResampler.Resample16kTo8k(pcm16k);

                    // Encode PCM → G.711 µ-law
                    var mulaw = new byte[pcm8k.Length / 2];
                    for (int i = 0; i < mulaw.Length; i++)
                    {
                        short sample = (short)(pcm8k[i * 2] | (pcm8k[i * 2 + 1] << 8));
                        mulaw[i] = MuLawEncoder.LinearToMuLaw(sample);
                    }

                    // Send via RTP
                    _rtpSession?.SendAudioFrame(
                        (uint)(_config.PcmFrameMs * 8), // timestamp increment: 20ms × 8kHz = 160
                        (int)SDPWellKnownMediaFormatsEnum.PCMU,
                        mulaw);

                    Interlocked.Increment(ref _rtpPacketsSent);
                    Interlocked.Increment(ref _wsFramesProcessed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing inbound audio frame");
                }
            }
            else
            {
                // No frames available, brief sleep to avoid busy-waiting
                await Task.Delay(5, ct);
            }
        }
    }

    /// <summary>Receives RTP from HT801 (phone mic) → G.711 decode → resample → PCM → WebSocket.</summary>
    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio) return;

        try
        {
            var mulaw = rtpPacket.Payload;

            // Decode G.711 µ-law → PCM 8kHz
            var pcm8k = new byte[mulaw.Length * 2];
            for (int i = 0; i < mulaw.Length; i++)
            {
                short sample = MuLawEncoder.MuLawToLinear(mulaw[i]);
                pcm8k[i * 2] = (byte)(sample & 0xFF);
                pcm8k[i * 2 + 1] = (byte)(sample >> 8);
            }

            // Resample 8kHz → 16kHz
            var pcm16k = AudioResampler.Resample8kTo16k(pcm8k);

            // Send to Chrome extension via WebSocket
            _ = _bridgeService.SendAudioFrameAsync(pcm16k);

            Interlocked.Increment(ref _rtpPacketsReceived);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing outbound audio frame");
        }
    }
}

/// <summary>G.711 µ-law encoder/decoder.</summary>
internal static class MuLawEncoder
{
    private const int BIAS = 0x84;
    private const int MAX = 32635;

    public static byte LinearToMuLaw(short sample)
    {
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > MAX) sample = MAX;
        sample = (short)(sample + BIAS);

        int exponent = 7;
        for (int expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte mulaw = (byte)(~(sign | (exponent << 4) | mantissa));
        return mulaw;
    }

    public static short MuLawToLinear(byte mulaw)
    {
        mulaw = (byte)~mulaw;
        int sign = mulaw & 0x80;
        int exponent = (mulaw >> 4) & 0x07;
        int mantissa = mulaw & 0x0F;
        int sample = ((mantissa << 3) + BIAS) << exponent;
        return (short)(sign != 0 ? -sample : sample);
    }
}

public record AudioBridgeStats(
    int RtpPacketsSent,
    int RtpPacketsReceived,
    int WsFramesProcessed,
    int QueueDepth,
    bool IsActive
);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GVAudioBridgeService" -v minimal`
Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Services/GVAudioBridgeService.cs src/RotaryPhoneController.GVBridge.Tests/GVAudioBridgeServiceTests.cs
git commit -m "feat(gvbridge): add GVAudioBridgeService for WebSocket PCM ↔ RTP G.711 bridging"
```

---

## Task 3: Wire GVAudioBridgeService into DI and GVBrowserAdapter

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Adapters/GVBrowserAdapter.cs` (lines 23, 29-47)
- Modify: `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs` (lines 17-34)

**References:**
- Existing constructor: `GVBrowserAdapter(GVBridgeService bridgeService, ILogger<GVBrowserAdapter> logger)` (line 23)
- Existing event wiring in `ActivateAsync()` (lines 37, 38-42)

- [ ] **Step 1: Update GVBridgeServiceExtensions to register GVAudioBridgeService**

In `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`, add after the `GVBridgeService` registration (after line 24):

```csharp
services.AddSingleton<GVAudioBridgeService>();
```

Add the using: `using RotaryPhoneController.GVBridge.Services;` (should already be present).

- [ ] **Step 2: Update GVBrowserAdapter constructor and event wiring**

In `src/RotaryPhoneController.GVBridge/Adapters/GVBrowserAdapter.cs`:

Add field and update constructor (line 23):
```csharp
private readonly GVAudioBridgeService _audioBridge;

public GVBrowserAdapter(GVBridgeService bridgeService, GVAudioBridgeService audioBridge, ILogger<GVBrowserAdapter> logger)
{
    _bridgeService = bridgeService;
    _audioBridge = audioBridge;
    _logger = logger;
}
```

Update `ActivateAsync()` — replace the `OnCallAnswered` lambda (line 37). Note: use `msg` as parameter name, not `_`, to avoid shadowing the discard operator:
```csharp
_bridgeService.OnCallAnswered += msg => {
    OnCallAnswered?.Invoke();
    Task.Run(() => _audioBridge.StartAsync());
};
```

Replace the `OnCallEnded` lambda (lines 38-42):
```csharp
_bridgeService.OnCallEnded += msg => {
    _activeCallId = null;
    OnCallEnded?.Invoke();
    Task.Run(() => _audioBridge.StopAsync());
};
```

- [ ] **Step 3: Build and run existing tests**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Then: `dotnet test src/RotaryPhoneController.GVBridge.Tests -v minimal`
Expected: Build succeeds. Existing `GVBrowserAdapterTests` will need a constructor update for the new `GVAudioBridgeService` parameter. Since `GVAudioBridgeService` is a concrete class (no interface), construct a real instance with test config (same approach as Task 2 tests — use `LocalRtpPort = 0` so no port conflict). Update the adapter construction in the test setup accordingly.

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Adapters/GVBrowserAdapter.cs src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs
git commit -m "feat(gvbridge): wire GVAudioBridgeService into adapter and DI"
```

---

## Task 4: SIP Message Event Hooks in SIPSorceryAdapter

**Files:**
- Create: `src/RotaryPhoneController.Core/Diagnostics/SipMessageEntry.cs`
- Modify: `src/RotaryPhoneController.Core/SIPSorceryAdapter.cs` (lines 74-112, 337-410)

**References:**
- `OnSIPRequestReceived()` (line 74) — handles REGISTER, NOTIFY, INVITE, BYE, OPTIONS
- `SendInviteToHT801()` (line 337) — port 49000 hardcoded at lines 240 and 382
- `OnSIPResponseReceived()` (line 114) — handles 200 OK responses

- [ ] **Step 1: Create diagnostic data models**

Create `src/RotaryPhoneController.Core/Diagnostics/SipMessageEntry.cs`:

```csharp
namespace RotaryPhoneController.Core.Diagnostics;

public enum SipDirection { Sent, Received }

public record SipMessageEntry(
    DateTime Timestamp,
    SipDirection Direction,
    string Method,
    string FromAddress,
    string ToAddress,
    int? StatusCode,
    string? StatusText,
    string? DiagnosticNote,
    string? CallId
);

public record CallTimelineEntry(
    DateTime Timestamp,
    string EventType,
    string Description,
    Dictionary<string, string>? Metadata
);

public record Ht801HealthStatus(
    bool IsReachable,
    double? PingMs,
    bool IsRegistered,
    int? RegistrationExpiresIn,
    DateTime? LastRegisterReceived,
    string? HookState,
    string? FirmwareVersion
);

public record ConfigParameter(
    string Name,
    string PCode,
    string Expected,
    string? Actual,
    bool IsMatch
);
```

- [ ] **Step 2: Add OnSipMessageLogged event to SIPSorceryAdapter**

In `src/RotaryPhoneController.Core/SIPSorceryAdapter.cs`, add the event declaration near the other events (around line 30):

```csharp
public event Action<SipMessageEntry>? OnSipMessageLogged;
```

Add a helper method:
```csharp
private void LogSipMessage(SipDirection direction, string method, string from, string to,
    int? statusCode = null, string? statusText = null, string? callId = null, string? note = null)
{
    OnSipMessageLogged?.Invoke(new SipMessageEntry(
        DateTime.UtcNow, direction, method, from, to, statusCode, statusText, note, callId));
}
```

Add using: `using RotaryPhoneController.Core.Diagnostics;`

- [ ] **Step 3: Instrument OnSIPRequestReceived**

In `OnSIPRequestReceived()` (line 74), add `LogSipMessage` calls at each handler:

- After REGISTER handling: `LogSipMessage(SipDirection.Received, "REGISTER", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);`
- After NOTIFY handling: `LogSipMessage(SipDirection.Received, "NOTIFY", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);`
- After INVITE handling: `LogSipMessage(SipDirection.Received, "INVITE", remoteEndPoint.ToString(), sipRequest.URI.ToString(), callId: sipRequest.Header.CallId);`
- After BYE handling: `LogSipMessage(SipDirection.Received, "BYE", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);`
- After OPTIONS handling: `LogSipMessage(SipDirection.Received, "OPTIONS", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);`

- [ ] **Step 4: Instrument SendInviteToHT801 with configurable RTP port**

In `SendInviteToHT801()` (line 337), change signature to:
```csharp
public void SendInviteToHT801(string extensionToRing, string targetIP, int localRtpPort = 49000)
```

Replace the hardcoded port 49000 at line 382 with the parameter:
```csharp
var localEndpoint = new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Parse(localIP), localRtpPort);
```

Add logging after sending the INVITE:
```csharp
LogSipMessage(SipDirection.Sent, "INVITE", $"{localIP}:{localRtpPort}", $"sip:{extensionToRing}@{targetIP}",
    callId: inviteRequest.Header.CallId);
```

- [ ] **Step 5: Instrument response handler**

In `OnSIPResponseReceived()` (line 114), add logging for every response:
```csharp
LogSipMessage(SipDirection.Received, sipResponse.Header.CSeqMethod.ToString(),
    remoteEndPoint.ToString(), localSIPEndPoint.ToString(),
    sipResponse.StatusCode, sipResponse.ReasonPhrase,
    callId: sipResponse.Header.CallId);
```

- [ ] **Step 6: Build and run existing SIP tests**

Run: `dotnet test src/RotaryPhoneController.Tests --filter "FullyQualifiedName~SipAdapter" -v minimal`
Expected: All existing tests pass (the new parameter has a default value so callers don't break).

- [ ] **Step 7: Commit**

```bash
git add src/RotaryPhoneController.Core/Diagnostics/ src/RotaryPhoneController.Core/SIPSorceryAdapter.cs
git commit -m "feat(diagnostics): add SIP message event hooks and diagnostic data models"
```

---

## Task 5: SipDiagnosticService

**Files:**
- Create: `src/RotaryPhoneController.Core/Diagnostics/SipDiagnosticService.cs`
- Create: `src/RotaryPhoneController.Tests/SipDiagnosticServiceTests.cs`

**References:**
- `SIPSorceryAdapter.OnSipMessageLogged` event — from Task 4
- `HT801ConfigService.ValidateDeviceAsync()` — line 174 of `HT801ConfigService.cs`
- `HT801ValidationResult` / `HT801ValidationItem` — lines 83-101

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.Tests/SipDiagnosticServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core.Diagnostics;
using Xunit;

namespace RotaryPhoneController.Tests;

public class SipDiagnosticServiceTests
{
    private readonly SipDiagnosticService _service;

    public SipDiagnosticServiceTests()
    {
        _service = new SipDiagnosticService(Mock.Of<ILogger<SipDiagnosticService>>());
    }

    [Fact]
    public void HandleSipMessage_AddsToLog()
    {
        var entry = new SipMessageEntry(DateTime.UtcNow, SipDirection.Received, "REGISTER",
            "192.168.86.250:5060", "0.0.0.0:5060", 200, "OK", null, "call-1");

        _service.HandleSipMessage(entry);

        var log = _service.GetRecentMessages(10);
        Assert.Single(log);
        Assert.Equal("REGISTER", log[0].Method);
    }

    [Fact]
    public void HandleSipMessage_RingBufferLimitsTo200()
    {
        for (int i = 0; i < 250; i++)
        {
            _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
                "OPTIONS", "a", "b", 200, "OK", null, $"call-{i}"));
        }

        var log = _service.GetRecentMessages(300);
        Assert.Equal(200, log.Count);
    }

    [Fact]
    public void HandleSipMessage_RegisterUpdatesRegistrationState()
    {
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
            "REGISTER", "192.168.86.250", "0.0.0.0:5060", null, null, null, null));

        var health = _service.GetHt801Health();
        Assert.True(health.IsRegistered);
        Assert.NotNull(health.LastRegisterReceived);
    }

    [Fact]
    public async Task DetectInviteTimeout_GeneratesDiagnosis()
    {
        string? diagnosisIssue = null;
        _service.OnDiagnosisGenerated += (issue, suggestions) => diagnosisIssue = issue;

        // Simulate INVITE sent
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow.AddSeconds(-6), SipDirection.Sent,
            "INVITE", "local", "sip:1000@192.168.86.250", null, null, null, "call-timeout"));

        // Check for timeout (no 180 Ringing received within 5s)
        _service.CheckInviteTimeouts();

        Assert.NotNull(diagnosisIssue);
        Assert.Contains("INVITE", diagnosisIssue);
    }

    [Fact]
    public void GetRecentMessages_FiltersByMethod()
    {
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
            "REGISTER", "a", "b", null, null, null, null));
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Sent,
            "INVITE", "a", "b", null, null, null, null));
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
            "REGISTER", "a", "b", null, null, null, null));

        var invites = _service.GetRecentMessages(10, "INVITE");
        Assert.Single(invites);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.Tests --filter "FullyQualifiedName~SipDiagnosticService" -v minimal`
Expected: Build error — `SipDiagnosticService` not found.

- [ ] **Step 3: Implement SipDiagnosticService**

Create `src/RotaryPhoneController.Core/Diagnostics/SipDiagnosticService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Diagnostics;

public class SipDiagnosticService : IHostedService, IDisposable
{
    private readonly ILogger<SipDiagnosticService> _logger;
    private readonly List<SipMessageEntry> _messageLog = new();
    private readonly List<CallTimelineEntry> _timeline = new();
    private readonly object _lock = new();
    private const int MaxLogSize = 200;

    // INVITE tracking for timeout detection
    private readonly Dictionary<string, DateTime> _pendingInvites = new(); // callId → sentTime

    // HT801 registration tracking
    private DateTime? _lastRegisterReceived;
    private int? _registerExpiry;
    private Timer? _timeoutTimer;

    public event Action<SipMessageEntry>? OnSipMessageLogged;
    public event Action<string, string[]>? OnDiagnosisGenerated;
    public event Action<Ht801HealthStatus>? OnHt801HealthUpdate;
    public event Action<CallTimelineEntry>? OnCallTimelineEvent;

    public SipDiagnosticService(ILogger<SipDiagnosticService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Check for INVITE timeouts every 3 seconds
        _timeoutTimer = new Timer(_ => CheckInviteTimeouts(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        _logger.LogInformation("SipDiagnosticService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timeoutTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timeoutTimer?.Dispose();

    public void HandleSipMessage(SipMessageEntry entry)
    {
        lock (_lock)
        {
            _messageLog.Add(entry);
            if (_messageLog.Count > MaxLogSize)
                _messageLog.RemoveAt(0);

            // Track REGISTER for HT801 health
            if (entry.Method == "REGISTER" && entry.Direction == SipDirection.Received)
            {
                _lastRegisterReceived = entry.Timestamp;
                _registerExpiry = 3600; // Default, could parse from SIP header
            }

            // Track outgoing INVITEs for timeout detection
            if (entry.Method == "INVITE" && entry.Direction == SipDirection.Sent && entry.CallId != null)
            {
                _pendingInvites[entry.CallId] = entry.Timestamp;

                AddTimelineEvent("InviteSent", $"INVITE sent to {entry.ToAddress}",
                    new Dictionary<string, string> { ["callId"] = entry.CallId });
            }

            // Track INVITE responses
            if (entry.Direction == SipDirection.Received && entry.CallId != null &&
                _pendingInvites.ContainsKey(entry.CallId))
            {
                if (entry.StatusCode == 180)
                {
                    AddTimelineEvent("Ringing", "Phone ringing (180 Ringing received)");
                    _pendingInvites.Remove(entry.CallId);
                }
                else if (entry.StatusCode == 200)
                {
                    AddTimelineEvent("Answered", "Call answered (200 OK received)");
                    _pendingInvites.Remove(entry.CallId);
                }
                else if (entry.StatusCode >= 400)
                {
                    var note = $"INVITE failed: {entry.StatusCode} {entry.StatusText}";
                    AddTimelineEvent("Error", note);
                    _pendingInvites.Remove(entry.CallId);

                    OnDiagnosisGenerated?.Invoke(note, GetSuggestionsForStatusCode(entry.StatusCode.Value));
                }
            }

            // Track BYE
            if (entry.Method == "BYE")
            {
                AddTimelineEvent("Ended", $"Call ended (BYE {entry.Direction.ToString().ToLower()})");
            }

            // Track NOTIFY with hook state
            if (entry.Method == "NOTIFY" && entry.Direction == SipDirection.Received)
            {
                AddTimelineEvent("HookChange", $"NOTIFY received from {entry.FromAddress}");
            }
        }

        OnSipMessageLogged?.Invoke(entry);
    }

    public void CheckInviteTimeouts()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var timedOut = _pendingInvites
                .Where(kv => (now - kv.Value).TotalSeconds > 5)
                .ToList();

            foreach (var kv in timedOut)
            {
                _pendingInvites.Remove(kv.Key);

                var issue = $"INVITE {kv.Key} sent but no 180 Ringing within 5s — phone did not ring";
                var suggestions = new[]
                {
                    "Check HT801 SIP registration is active",
                    "Verify SIP extension matches HT801 config",
                    "Check codec negotiation (PCMU required)",
                    "Verify HT801 is reachable on the network",
                    "Check SDP port matches the audio bridge listener"
                };

                _logger.LogWarning("{Issue}", issue);
                AddTimelineEvent("Error", issue);
                OnDiagnosisGenerated?.Invoke(issue, suggestions);
            }
        }
    }

    public List<SipMessageEntry> GetRecentMessages(int count, string? methodFilter = null)
    {
        lock (_lock)
        {
            IEnumerable<SipMessageEntry> query = _messageLog;
            if (methodFilter != null)
                query = query.Where(m => m.Method == methodFilter);
            return query.TakeLast(count).ToList();
        }
    }

    public List<CallTimelineEntry> GetTimeline(int count = 50)
    {
        lock (_lock)
        {
            return _timeline.TakeLast(count).ToList();
        }
    }

    public Ht801HealthStatus GetHt801Health()
    {
        int? expiresIn = null;
        if (_lastRegisterReceived.HasValue && _registerExpiry.HasValue)
        {
            var elapsed = (DateTime.UtcNow - _lastRegisterReceived.Value).TotalSeconds;
            expiresIn = Math.Max(0, (int)(_registerExpiry.Value - elapsed));
        }

        return new Ht801HealthStatus(
            IsReachable: _lastRegisterReceived.HasValue,
            PingMs: null, // Set by periodic health check
            IsRegistered: _lastRegisterReceived.HasValue &&
                          (DateTime.UtcNow - _lastRegisterReceived.Value).TotalSeconds < (_registerExpiry ?? 3600),
            RegistrationExpiresIn: expiresIn,
            LastRegisterReceived: _lastRegisterReceived,
            HookState: null, // Set from NOTIFY events
            FirmwareVersion: null // Set from HT801 config read
        );
    }

    private void AddTimelineEvent(string eventType, string description,
        Dictionary<string, string>? metadata = null)
    {
        var entry = new CallTimelineEntry(DateTime.UtcNow, eventType, description, metadata);
        _timeline.Add(entry);
        if (_timeline.Count > 200)
            _timeline.RemoveAt(0);
        OnCallTimelineEvent?.Invoke(entry);
    }

    private static string[] GetSuggestionsForStatusCode(int statusCode) => statusCode switch
    {
        401 => new[] { "Check SIP authentication credentials on HT801", "Verify Auth ID and password match" },
        403 => new[] { "Extension or domain mismatch", "Check SIP User ID matches the INVITE target" },
        408 => new[] { "HT801 not responding", "Check network connectivity", "Verify IP address is correct" },
        480 => new[] { "Device not registered or offline", "Restart HT801", "Check registration status" },
        486 => new[] { "Phone is busy (off-hook)", "Hang up and try again" },
        503 => new[] { "Device overloaded or misconfigured", "Restart HT801" },
        _ => new[] { $"Unexpected SIP error {statusCode}", "Check HT801 logs" }
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.Tests --filter "FullyQualifiedName~SipDiagnosticService" -v minimal`
Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Core/Diagnostics/SipDiagnosticService.cs src/RotaryPhoneController.Tests/SipDiagnosticServiceTests.cs
git commit -m "feat(diagnostics): add SipDiagnosticService with INVITE timeout detection"
```

---

## Task 6: Wire Diagnostics into Server (DI, SignalR, SIP Adapter)

**Files:**
- Modify: `src/RotaryPhoneController.Server/Program.cs`
- Modify: `src/RotaryPhoneController.Server/Services/SignalRNotifierService.cs`
- Modify: `src/RotaryPhoneController.Core/HT801/HT801ConfigService.cs`

**References:**
- `SignalRNotifierService.cs` — event subscription pattern (lines 44-65), broadcasting via `_hubContext.Clients.All.SendAsync` (lines 63-66, 79, 85, 135)
- `HT801ConfigService.ValidateDeviceAsync()` (line 174), `HT801ValidationItem` (lines 93-101)

- [ ] **Step 1: Register SipDiagnosticService in Program.cs**

In `src/RotaryPhoneController.Server/Program.cs`, add after existing service registrations:

```csharp
builder.Services.AddSingleton<SipDiagnosticService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SipDiagnosticService>());
```

Add using: `using RotaryPhoneController.Core.Diagnostics;`

- [ ] **Step 2: Wire SIPSorceryAdapter events to SipDiagnosticService**

In `Program.cs`, after service provider is built (after `var app = builder.Build()`), add:

```csharp
// Wire SIP diagnostic events
var sipAdapter = app.Services.GetRequiredService<SIPSorceryAdapter>();
var sipDiagnostics = app.Services.GetRequiredService<SipDiagnosticService>();
sipAdapter.OnSipMessageLogged += sipDiagnostics.HandleSipMessage;
```

- [ ] **Step 3: Add diagnostics broadcasting to SignalRNotifierService**

In `src/RotaryPhoneController.Server/Services/SignalRNotifierService.cs`:

First, add field and constructor parameter:
```csharp
private readonly SipDiagnosticService _diagnostics;

// Add to constructor parameter list:
// ..., SipDiagnosticService diagnostics)
// Add to constructor body:
// _diagnostics = diagnostics;
```

Then subscribe to events in `StartAsync`:
```csharp
_diagnostics.OnSipMessageLogged += entry =>
    _hubContext.Clients.All.SendAsync("SipMessage", entry);

_diagnostics.OnDiagnosisGenerated += (issue, suggestions) =>
    _hubContext.Clients.All.SendAsync("SipDiagnosis", issue, suggestions);

_diagnostics.OnHt801HealthUpdate += status =>
    _hubContext.Clients.All.SendAsync("Ht801Health", status);

_diagnostics.OnCallTimelineEvent += entry =>
    _hubContext.Clients.All.SendAsync("CallTimeline", entry);
```

- [ ] **Step 4: Add CompareConfigAsync to HT801ConfigService**

In `src/RotaryPhoneController.Core/HT801/HT801ConfigService.cs`, add:

```csharp
public async Task<List<ConfigParameter>> CompareConfigAsync(string phoneId)
{
    var result = await ValidateDeviceAsync(phoneId, autoFix: false);
    return result.Items.Select(item => new ConfigParameter(
        item.Setting, item.PValue, item.Expected, item.Actual, item.Match
    )).ToList();
}
```

Add using: `using RotaryPhoneController.Core.Diagnostics;`

- [ ] **Step 5: Build the full solution**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeds with no errors.

- [ ] **Step 6: Commit**

```bash
git add src/RotaryPhoneController.Server/Program.cs src/RotaryPhoneController.Server/Services/SignalRNotifierService.cs src/RotaryPhoneController.Core/HT801/HT801ConfigService.cs
git commit -m "feat(diagnostics): wire SipDiagnosticService into DI, SignalR, and HT801 config"
```

---

## Task 7: DiagnosticsController REST API

**Files:**
- Create: `src/RotaryPhoneController.Server/Controllers/DiagnosticsController.cs`

**References:**
- `src/RotaryPhoneController.Server/Controllers/PhoneController.cs` — existing controller patterns (lines 37-140)
- `SipDiagnosticService` — from Task 5
- `HT801ConfigService` — `CompareConfigAsync` from Task 6
- `SIPSorceryAdapter.SendInviteToHT801()` — with `localRtpPort` parameter from Task 4

- [ ] **Step 1: Create DiagnosticsController**

Create `src/RotaryPhoneController.Server/Controllers/DiagnosticsController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Diagnostics;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly SipDiagnosticService _diagnostics;
    private readonly HT801ConfigService _ht801Config;
    private readonly SIPSorceryAdapter _sipAdapter;
    private readonly GVBridgeService _gvBridge;
    private readonly GVAudioBridgeService _audioBridge;

    public DiagnosticsController(
        SipDiagnosticService diagnostics,
        HT801ConfigService ht801Config,
        SIPSorceryAdapter sipAdapter,
        GVBridgeService gvBridge,
        GVAudioBridgeService audioBridge)
    {
        _diagnostics = diagnostics;
        _ht801Config = ht801Config;
        _sipAdapter = sipAdapter;
        _gvBridge = gvBridge;
        _audioBridge = audioBridge;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            Ht801Health = _diagnostics.GetHt801Health(),
            GvBridge = new
            {
                _gvBridge.IsExtensionConnected,
                _gvBridge.ExtensionVersion,
                AudioBridgeActive = _audioBridge.IsActive
            },
            RecentSipMessages = _diagnostics.GetRecentMessages(10),
            Timeline = _diagnostics.GetTimeline(20)
        });
    }

    [HttpGet("sip-log")]
    public IActionResult GetSipLog([FromQuery] int count = 50, [FromQuery] string? method = null)
    {
        return Ok(_diagnostics.GetRecentMessages(count, method));
    }

    [HttpGet("timeline")]
    public IActionResult GetTimeline([FromQuery] int count = 50)
    {
        return Ok(_diagnostics.GetTimeline(count));
    }

    [HttpPost("test-ring")]
    public IActionResult TestRing([FromQuery] string phoneId = "default")
    {
        try
        {
            // Use the configured phone to send a test INVITE
            _sipAdapter.SendInviteToHT801("1000", "192.168.86.250");
            return Ok(new { message = "Test INVITE sent. Check SIP log for response." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("test-audio")]
    public async Task<IActionResult> TestAudio()
    {
        try
        {
            // Send a 1kHz test tone for 2 seconds via RTP to HT801
            if (!_audioBridge.IsActive)
                await _audioBridge.StartAsync();

            // Generate 2 seconds of 1kHz sine wave at 8kHz sample rate
            var samples = new byte[16000]; // 2 seconds × 8000 samples/sec
            for (int i = 0; i < 16000; i++)
            {
                var value = (short)(short.MaxValue / 2 * Math.Sin(2 * Math.PI * 1000 * i / 8000.0));
                var mulaw = (byte)~((value < 0 ? 0x80 : 0x00) | (((Math.Min(Math.Abs(value) + 132, 32767)) >> ((Math.Min(Math.Abs(value) + 132, 32767)).ToString("X").Length > 3 ? 10 : 4)) & 0x0F));
                samples[i] = mulaw;
            }

            // TODO: Send via _audioBridge RTP session — for now, just confirm the bridge started
            return Ok(new { message = "Audio bridge active. Test tone generation is a placeholder — full implementation sends RTP frames." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("ht801/config")]
    public async Task<IActionResult> GetHt801Config([FromQuery] string phoneId = "default")
    {
        try
        {
            var comparison = await _ht801Config.CompareConfigAsync(phoneId);
            return Ok(comparison);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("ht801/validate")]
    public async Task<IActionResult> ValidateHt801([FromQuery] string phoneId = "default", [FromQuery] bool autoFix = false)
    {
        try
        {
            var result = await _ht801Config.ValidateDeviceAsync(phoneId, autoFix);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
```

- [ ] **Step 2: Build and verify endpoint registration**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Builds successfully.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Server/Controllers/DiagnosticsController.cs
git commit -m "feat(diagnostics): add DiagnosticsController REST API"
```

---

## Task 8: React Diagnostics Page — SignalR Hook and API

**Files:**
- Create: `src/RotaryPhoneController.Client/src/hooks/useDiagnostics.ts`
- Modify: `src/RotaryPhoneController.Client/src/services/api.ts`

**References:**
- `src/RotaryPhoneController.Client/src/services/SignalRService.ts` — existing SignalR connection patterns
- `src/RotaryPhoneController.Client/src/hooks/useGVBridge.ts` — existing hook patterns
- `src/RotaryPhoneController.Client/src/services/api.ts` — existing API call patterns

- [ ] **Step 1: Add diagnostics API functions**

In `src/RotaryPhoneController.Client/src/services/api.ts`, add:

```typescript
export const getDiagnosticsStatus = () => api.get('/diagnostics/status');
export const getSipLog = (count = 50, method?: string) =>
  api.get('/diagnostics/sip-log', { params: { count, method } });
export const getTimeline = (count = 50) =>
  api.get('/diagnostics/timeline', { params: { count } });
export const testRing = () => api.post('/diagnostics/test-ring');
export const getHt801Config = () => api.get('/diagnostics/ht801/config');
export const validateHt801 = (autoFix = false) =>
  api.post('/diagnostics/ht801/validate', null, { params: { autoFix } });
```

- [ ] **Step 2: Create useDiagnostics hook**

Create `src/RotaryPhoneController.Client/src/hooks/useDiagnostics.ts`:

```typescript
import { useState, useEffect, useCallback } from 'react';
import { signalRService } from '../services/SignalRService';
import { getDiagnosticsStatus } from '../services/api';

export interface SipMessageEntry {
  timestamp: string;
  direction: 'Sent' | 'Received';
  method: string;
  fromAddress: string;
  toAddress: string;
  statusCode: number | null;
  statusText: string | null;
  diagnosticNote: string | null;
  callId: string | null;
}

export interface CallTimelineEntry {
  timestamp: string;
  eventType: string;
  description: string;
  metadata: Record<string, string> | null;
}

export interface Ht801HealthStatus {
  isReachable: boolean;
  pingMs: number | null;
  isRegistered: boolean;
  registrationExpiresIn: number | null;
  lastRegisterReceived: string | null;
  hookState: string | null;
  firmwareVersion: string | null;
}

export interface DiagnosisAlert {
  issue: string;
  suggestions: string[];
  timestamp: string;
}

export function useDiagnostics() {
  const [sipMessages, setSipMessages] = useState<SipMessageEntry[]>([]);
  const [timeline, setTimeline] = useState<CallTimelineEntry[]>([]);
  const [ht801Health, setHt801Health] = useState<Ht801HealthStatus | null>(null);
  const [diagnoses, setDiagnoses] = useState<DiagnosisAlert[]>([]);
  const [gvBridgeStatus, setGvBridgeStatus] = useState<any>(null);

  // Load initial state
  useEffect(() => {
    getDiagnosticsStatus().then(res => {
      setSipMessages(res.data.recentSipMessages || []);
      setTimeline(res.data.timeline || []);
      setHt801Health(res.data.ht801Health || null);
      setGvBridgeStatus(res.data.gvBridge || null);
    }).catch(() => {});
  }, []);

  // Subscribe to real-time updates
  useEffect(() => {
    const connection = signalRService.connection;
    if (!connection) return;

    const onSipMessage = (entry: SipMessageEntry) => {
      setSipMessages(prev => [...prev.slice(-199), entry]);
    };
    const onTimeline = (entry: CallTimelineEntry) => {
      setTimeline(prev => [...prev.slice(-199), entry]);
    };
    const onHt801Health = (status: Ht801HealthStatus) => {
      setHt801Health(status);
    };
    const onDiagnosis = (issue: string, suggestions: string[]) => {
      setDiagnoses(prev => [...prev.slice(-49), { issue, suggestions, timestamp: new Date().toISOString() }]);
    };

    connection.on('SipMessage', onSipMessage);
    connection.on('CallTimeline', onTimeline);
    connection.on('Ht801Health', onHt801Health);
    connection.on('SipDiagnosis', onDiagnosis);

    return () => {
      connection.off('SipMessage', onSipMessage);
      connection.off('CallTimeline', onTimeline);
      connection.off('Ht801Health', onHt801Health);
      connection.off('SipDiagnosis', onDiagnosis);
    };
  }, []);

  const clearDiagnoses = useCallback(() => setDiagnoses([]), []);

  return { sipMessages, timeline, ht801Health, diagnoses, gvBridgeStatus, clearDiagnoses };
}
```

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Client/src/hooks/useDiagnostics.ts src/RotaryPhoneController.Client/src/services/api.ts
git commit -m "feat(diagnostics): add useDiagnostics hook and API functions"
```

---

## Task 9: React Diagnostics Page — UI Components

**Files:**
- Create: `src/RotaryPhoneController.Client/src/pages/Diagnostics.tsx`
- Create: `src/RotaryPhoneController.Client/src/components/diagnostics/StatusBar.tsx`
- Create: `src/RotaryPhoneController.Client/src/components/diagnostics/SipMessageLog.tsx`
- Create: `src/RotaryPhoneController.Client/src/components/diagnostics/Ht801HealthPanel.tsx`
- Create: `src/RotaryPhoneController.Client/src/components/diagnostics/RtpStatsPanel.tsx`
- Create: `src/RotaryPhoneController.Client/src/components/diagnostics/CallTimeline.tsx`
- Modify: `src/RotaryPhoneController.Client/src/App.tsx` (add route, line ~28)

**References:**
- `src/RotaryPhoneController.Client/src/pages/GVBridge.tsx` — existing page patterns
- `src/RotaryPhoneController.Client/src/App.tsx` — router setup (lines 23-30)
- Mockup at `.superpowers/brainstorm/1337-1774198075/diagnostics-ui.html`

- [ ] **Step 1: Create StatusBar component**

Create `src/RotaryPhoneController.Client/src/components/diagnostics/StatusBar.tsx` — four status cards (HT801, SIP Server, GV Bridge, Call State) with green/yellow/red borders based on health status. Uses MUI Card, Typography, and Chip components.

- [ ] **Step 2: Create SipMessageLog component**

Create `src/RotaryPhoneController.Client/src/components/diagnostics/SipMessageLog.tsx` — scrolling log with method filter chips (INVITE/REGISTER/NOTIFY/BYE/ALL). Each entry shows timestamp, direction arrow, method (color-coded), addresses, status code. Failed transactions highlighted with red diagnostic annotations. Uses MUI Table or List with monospace font.

- [ ] **Step 3: Create Ht801HealthPanel component**

Create `src/RotaryPhoneController.Client/src/components/diagnostics/Ht801HealthPanel.tsx` — health summary (registration, ping, hook state, firmware). Action buttons: Test Ring, Validate Config, Read Config. Expandable config validation table showing parameter comparison (expected vs actual) with red/green status indicators and "Fix All Mismatches" button. Uses MUI Accordion, Table, Button.

- [ ] **Step 4: Create RtpStatsPanel component**

Create `src/RotaryPhoneController.Client/src/components/diagnostics/RtpStatsPanel.tsx` — shows "No active stream" when idle. During active calls, displays: packets sent/received per second, jitter, packet loss %, audio levels, codec info, WebSocket↔RTP frame count comparison. Subscribes to `RtpStats` SignalR event. Uses MUI Card with monospace text.

- [ ] **Step 5: Create CallTimeline component**

Create `src/RotaryPhoneController.Client/src/components/diagnostics/CallTimeline.tsx` — chronological event log with color-coded event types (InviteSent=blue, Ringing=yellow, Answered=green, Error=red, Ended=gray). Monospace font, compact layout. Uses MUI List.

- [ ] **Step 6: Create Diagnostics page and add route**

Create `src/RotaryPhoneController.Client/src/pages/Diagnostics.tsx` that composes StatusBar, SipMessageLog, Ht801HealthPanel, and CallTimeline using the `useDiagnostics` hook. Layout: StatusBar across top, SipMessageLog (60% width) left, stacked panels (40% width) right.

In `src/RotaryPhoneController.Client/src/App.tsx`, add route:
```tsx
import Diagnostics from './pages/Diagnostics';
// In routes:
<Route path="/diagnostics" element={<Diagnostics />} />
```

- [ ] **Step 7: Build React app**

Run: `cd src/RotaryPhoneController.Client && npm run build`
Expected: Build succeeds with no errors.

- [ ] **Step 8: Commit**

```bash
git add src/RotaryPhoneController.Client/src/pages/Diagnostics.tsx src/RotaryPhoneController.Client/src/components/diagnostics/ src/RotaryPhoneController.Client/src/App.tsx
git commit -m "feat(diagnostics): add Diagnostics page with SIP log, HT801 health, and call timeline"
```

---

## Task 10: Chrome Extension — Audio Bridge (offscreen document + tabCapture)

**Files:**
- Modify: `ChromeExtension/offscreen/offscreen.html`
- Modify: `ChromeExtension/offscreen/audio-bridge.js`
- Modify: `ChromeExtension/background/service-worker.js`
- Modify: `ChromeExtension/content/gv-bridge.js`

**References:**
- `ChromeExtension/manifest.json` — already has `tabCapture`, `offscreen` permissions
- `ChromeExtension/content/gv-bridge.js` — WebSocket connection and message relay
- `ChromeExtension/background/service-worker.js` — minimal service worker from earlier fix

- [ ] **Step 1: Update offscreen.html to load audio-bridge.js**

In `ChromeExtension/offscreen/offscreen.html`, ensure it loads the script:
```html
<!DOCTYPE html>
<html>
<head><title>GV Bridge Audio</title></head>
<body>
  <script src="audio-bridge.js"></script>
</body>
</html>
```

- [ ] **Step 2: Implement audio-bridge.js**

Implement `ChromeExtension/offscreen/audio-bridge.js` with:
- `startCapture(streamId)` — creates AudioContext, gets tab audio stream, creates AudioWorkletNode or ScriptProcessorNode, downsamples to 16kHz mono, chunks into 20ms frames, base64 encodes, sends via `chrome.runtime.sendMessage`
- `stopCapture()` — tears down stream and AudioContext
- `playAudioFrame(base64Pcm)` — decodes base64, creates AudioBuffer, schedules for gapless playback
- Listens for messages from service worker: `startCapture`, `stopCapture`, `audioFrame`

- [ ] **Step 3: Update service-worker.js for tabCapture and offscreen**

Add to `ChromeExtension/background/service-worker.js`:
- Handle `requestTabCapture` message: call `chrome.tabCapture.getMediaStreamId({ targetTabId })`, return stream ID
- Handle `createOffscreen` message: call `chrome.offscreen.createDocument(...)` if not already created
- Handle `audioFrame` from offscreen: relay to content script

- [ ] **Step 4: Update content/gv-bridge.js for audio relay**

Add to `ChromeExtension/content/gv-bridge.js`:
- On `callAnswered` bridge message: send `requestTabCapture` to service worker, then `startCapture` to offscreen doc
- Relay incoming `audioFrame` from WebSocket to offscreen doc for playback
- Relay outgoing `audioFrame` from offscreen doc to WebSocket
- On `callEnded`: send `stopCapture` to offscreen doc

- [ ] **Step 5: Test extension locally**

Load the updated extension in Chromium on the radio box. Navigate to voice.google.com. Verify service worker logs show offscreen document creation capability. Verify no errors in extension console.

- [ ] **Step 6: Deploy to radio box**

```bash
scp -r ChromeExtension/* mmack@radio:/home/mmack/snap/chromium/common/gv-bridge-profile/Extension/
ssh mmack@radio "systemctl --user restart gv-bridge-chrome"
```

- [ ] **Step 7: Commit**

```bash
git add ChromeExtension/
git commit -m "feat(extension): implement tabCapture audio bridge in offscreen document"
```

---

## Task 11: Full Integration Build and Deploy

**Files:**
- All files from Tasks 1-10

- [ ] **Step 1: Run all .NET tests**

Run: `dotnet test RotaryPhoneController.sln -v minimal`
Expected: All tests pass (existing + new).

- [ ] **Step 2: Build React app**

Run: `cd src/RotaryPhoneController.Client && npm run build`
Expected: Build succeeds.

- [ ] **Step 3: Publish and deploy to radio box**

Run: `pwsh -Command "./deploy/Deploy-ToLinux.ps1 -TargetHost radio -Runtime linux-x64"`
Expected: Deploy succeeds, service restarts.

- [ ] **Step 4: Deploy Chrome extension**

```bash
scp -r ChromeExtension/* mmack@radio:/home/mmack/snap/chromium/common/gv-bridge-profile/Extension/
ssh mmack@radio "systemctl --user restart gv-bridge-chrome"
```

- [ ] **Step 5: Verify diagnostics page**

Open `http://radio:5004/diagnostics` in browser. Verify:
- Status cards show HT801, SIP, GV Bridge status
- SIP message log populates with REGISTER/OPTIONS messages
- HT801 health shows registration status
- "Test Ring" button sends INVITE and shows result in SIP log

- [ ] **Step 6: Verify GV Bridge audio (manual test)**

1. Ensure GV Bridge Chrome extension is connected (check status)
2. Switch adapter mode to GVBrowser: `curl -X PUT http://radio:5004/api/gvbridge/adapter/mode -H "Content-Type: application/json" -d '"GVBrowser"'`
3. Place a test call to Google Voice number
4. Verify phone rings (check diagnostics page for INVITE→180→200 sequence)
5. Pick up handset, verify audio in both directions
6. Hang up, verify clean teardown in timeline

- [ ] **Step 7: Commit any fixes**

```bash
git add -A
git commit -m "fix: integration fixes from end-to-end testing"
```
