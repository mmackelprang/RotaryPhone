# SIP-over-WebSocket + DTLS-SRTP Integration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Chrome extension audio with direct SIP-over-WebSocket + DTLS-SRTP to Google Voice, eliminating the Chrome dependency entirely.

**Architecture:** Port the working `SipWssCallTransport` from GVResearch into RotaryPhone's GVBridge. Rewire `GVApiAdapter` to use SIP INVITE/BYE instead of HTTP API + extension. Replace the audio bridge's extension audio source with SIP transport Opus audio resampled from 48kHz to 8kHz G.711.

**Tech Stack:** SIPSorcery 10.0.6-diag (custom DTLS build), Concentus (Opus codec), Playwright (cookie extraction), .NET 10

**Spec:** `docs/superpowers/specs/2026-03-30-sip-wss-dtls-srtp-integration-design.md`

---

## File Structure

### New files (in `src/RotaryPhoneController.GVBridge/`)

```
Sip/
  GvSipTransport.cs          — SIP signaling + DTLS-SRTP + Opus (copied from GVResearch SipWssCallTransport.cs)
  GvSipWebSocketChannel.cs   — WebSocket with sip subprotocol (copied from GVResearch)
  GvSipCredentialProvider.cs  — SIP credential fetcher via sipregisterinfo/get (copied from GVResearch)
Auth/
  GvCookieSet.cs             — Cookie model with raw header (copied from GVResearch)
  CookieRetriever.cs         — Chrome CDP cookie extraction (copied from GVResearch)
```

### Modified files

```
RotaryPhoneController.GVBridge.csproj   — SIPSorcery 10.0.6-diag, add Concentus
Audio/AudioResampler.cs                 — Add Resample48kTo8k / Resample8kTo48k
Services/GVAudioBridgeService.cs        — Replace extension audio with SIP transport audio
Adapters/GVApiAdapter.cs                — Replace extension + HTTP API with SIP transport
Extensions/GVBridgeServiceExtensions.cs — Remove old services, register new ones
Models/GVBridgeConfig.cs                — Remove extension config, add SIP config
Auth/GvCookieStore.cs                   — Adapt to work with GvCookieSet
Auth/GvHttpClientHandler.cs             — Use GvCookieSet.ToCookieHeader()
```

### New files at repo root

```
nuget.config                            — Add local-packages source
local-packages/SIPSorcery.10.0.6-diag.nupkg
local-packages/SIPSorceryMedia.Abstractions.10.0.6-diag.nupkg
```

### Deleted files

```
Adapters/  — (none, GVApiAdapter is modified not deleted)
Services/GVBridgeService.cs             — Chrome extension WebSocket server
Models/ExtensionMessage.cs              — Extension message types
Api/GVBridgeHub.cs                      — SignalR hub
Auth/GvCookieJar.cs                     — Replaced by GvCookieSet
Auth/GvCookieRotationService.cs         — Replaced by CDP-based cookie refresh
Tools/GvLoginTool.cs                    — Replaced by CookieRetriever
Clients/GvCallClient.cs                 — Replaced by SIP INVITE/BYE
Clients/GvSmsClient.cs                  — Deferred, not needed for calls
Signaler/GvSignalerClient.cs            — Replaced by SIP INVITE reception
Signaler/SignalerEvent.cs               — Replaced by SIP events
ChromeExtension/                        — Entire directory
```

---

## Task 1: Package Setup

**Files:**
- Create: `nuget.config` (repo root)
- Create: `local-packages/` directory with NuGet packages
- Modify: `src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`

- [ ] **Step 1: Copy modified SIPSorcery packages**

```bash
mkdir -p local-packages
cp "D:/prj/GVResearch/local-packages/SIPSorcery.10.0.6-diag.nupkg" local-packages/
cp "D:/prj/GVResearch/local-packages/SIPSorceryMedia.Abstractions.10.0.6-diag.nupkg" local-packages/
```

- [ ] **Step 2: Create nuget.config**

Create `nuget.config` at repo root:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="local-packages" value="./local-packages" />
  </packageSources>
</configuration>
```

- [ ] **Step 3: Update GVBridge csproj**

In `src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`, replace the SIPSorcery reference and add Concentus:

Replace:
```xml
<PackageReference Include="SIPSorcery" Version="8.0.23" />
```

With:
```xml
<PackageReference Include="SIPSorcery" Version="10.0.6-diag" />
<PackageReference Include="Concentus" Version="2.*" />
```

- [ ] **Step 4: Restore and build**

Run: `dotnet restore src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj && dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`

Expected: Build may have breaking changes from SIPSorcery upgrade — fix any API changes. Common changes between 8.x and 10.x: `MediaStreamTrack` constructor changes, `RTPSession` API changes.

- [ ] **Step 5: Commit**

```bash
git add nuget.config local-packages/ src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj
git commit -m "chore: upgrade SIPSorcery to 10.0.6-diag, add Concentus for Opus

Custom SIPSorcery build removes encrypt_then_mac and status_request
DTLS extensions that Google Voice rejects. Concentus provides Opus
codec for 48kHz audio encode/decode.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Copy SIP Transport Files

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Sip/GvSipTransport.cs`
- Create: `src/RotaryPhoneController.GVBridge/Sip/GvSipWebSocketChannel.cs`
- Create: `src/RotaryPhoneController.GVBridge/Sip/GvSipCredentialProvider.cs`

- [ ] **Step 1: Copy SipWssCallTransport.cs as GvSipTransport.cs**

```bash
cp "D:/prj/GVResearch/src/GvResearch.Sip/Transport/SipWssCallTransport.cs" \
   "src/RotaryPhoneController.GVBridge/Sip/GvSipTransport.cs"
```

Then in `GvSipTransport.cs`:
- Change namespace from `GvResearch.Sip.Transport` to `RotaryPhoneController.GVBridge.Sip`
- Rename class from `SipWssCallTransport` to `GvSipTransport`
- Replace any `using GvResearch.*` with equivalent RotaryPhone namespaces
- Replace `using GvResearch.Shared.Models;` — define the needed types locally (see Step 4)
- Replace `using GvResearch.Shared.Auth;` with `using RotaryPhoneController.GVBridge.Auth;`

- [ ] **Step 2: Copy GvSipWebSocketChannel.cs**

```bash
cp "D:/prj/GVResearch/src/GvResearch.Sip/Transport/GvSipWebSocketChannel.cs" \
   "src/RotaryPhoneController.GVBridge/Sip/GvSipWebSocketChannel.cs"
```

Change namespace to `RotaryPhoneController.GVBridge.Sip`.

- [ ] **Step 3: Copy GvSipCredentialProvider.cs**

```bash
cp "D:/prj/GVResearch/src/GvResearch.Sip/Transport/GvSipCredentialProvider.cs" \
   "src/RotaryPhoneController.GVBridge/Sip/GvSipCredentialProvider.cs"
```

Change namespace to `RotaryPhoneController.GVBridge.Sip`.
Replace `using GvResearch.Shared.Auth;` with `using RotaryPhoneController.GVBridge.Auth;`.

- [ ] **Step 4: Define model types used by the transport**

The GVResearch transport references types from `GvResearch.Shared.Models` and `GvResearch.Shared.Services`. Define these in the Sip directory since they're small:

Create `src/RotaryPhoneController.GVBridge/Sip/SipModels.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Sip;

public sealed record SipCredentials(
    string SipUsername,
    string BearerToken,
    string PhoneNumber,
    int ExpirySeconds);

public sealed record TransportCallResult(
    string CallId,
    bool Success,
    string? ErrorMessage = null);

public sealed record TransportCallStatus(
    string CallId,
    CallStatusType Status);

public enum CallStatusType { Unknown, Ringing, Active, Completed, Failed }

public sealed class AudioDataEventArgs(string callId, ReadOnlyMemory<byte> pcmData, int sampleRate) : EventArgs
{
    public string CallId { get; } = callId;
    public ReadOnlyMemory<byte> PcmData { get; } = pcmData;
    public int SampleRate { get; } = sampleRate;
}

public sealed class IncomingCallEventArgs(IncomingCallInfo callInfo) : EventArgs
{
    public IncomingCallInfo CallInfo { get; } = callInfo;
}

public sealed record IncomingCallInfo(string CallId, string CallerNumber);
```

- [ ] **Step 5: Fix all using statements and references in the 3 copied files**

In `GvSipTransport.cs`:
- Remove `using GvResearch.Shared.Models;` and `using GvResearch.Shared.Services;`
- These types are now in `RotaryPhoneController.GVBridge.Sip` (same namespace)
- Remove any `ICallTransport` interface reference — make the class standalone (not implementing an interface from GVResearch)

In `GvSipCredentialProvider.cs`:
- The provider needs an `HttpClient` that has SAPISIDHASH auth. It will receive the authenticated HttpClient from the adapter.
- Fix any `GvResearch.Shared` references to point to local Auth namespace

- [ ] **Step 6: Build**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`

Fix any remaining compile errors from namespace/type mismatches. This may take several iterations.

- [ ] **Step 7: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Sip/
git commit -m "feat(sip): port SIP-over-WebSocket transport from GVResearch

Copy SipWssCallTransport (as GvSipTransport), GvSipWebSocketChannel,
and GvSipCredentialProvider. Adjust namespaces, define local model types.
Verified working for bidirectional Opus audio in GVResearch softphone.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Copy Cookie Management Files

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Auth/GvCookieSet.cs`
- Create: `src/RotaryPhoneController.GVBridge/Auth/CookieRetriever.cs`

- [ ] **Step 1: Copy GvCookieSet.cs**

```bash
cp "D:/prj/GVResearch/src/GvResearch.Shared/Auth/GvCookieSet.cs" \
   "src/RotaryPhoneController.GVBridge/Auth/GvCookieSet.cs"
```

Change namespace from `GvResearch.Shared.Auth` to `RotaryPhoneController.GVBridge.Auth`.

- [ ] **Step 2: Copy CookieRetriever.cs**

```bash
cp "D:/prj/GVResearch/src/GvResearch.Shared/Auth/CookieRetriever.cs" \
   "src/RotaryPhoneController.GVBridge/Auth/CookieRetriever.cs"
```

Change namespace to `RotaryPhoneController.GVBridge.Auth`.
Replace `using GvResearch.Shared.Auth;` — types are now in same namespace.
Replace `TokenEncryption.Encrypt` with local `GvCookieStore`-style encryption, OR copy `TokenEncryption.cs` as well.

The simplest approach: copy `TokenEncryption.cs` too:

```bash
cp "D:/prj/GVResearch/src/GvResearch.Shared/Auth/TokenEncryption.cs" \
   "src/RotaryPhoneController.GVBridge/Auth/TokenEncryption.cs"
```

Change namespace to `RotaryPhoneController.GVBridge.Auth`.

- [ ] **Step 3: Update GvCookieStore to work with GvCookieSet**

Modify `src/RotaryPhoneController.GVBridge/Auth/GvCookieStore.cs`:

Change `SaveAsync` and `LoadAsync` to accept/return `GvCookieSet` instead of `GvCookieJar`:

```csharp
public async Task SaveAsync(GvCookieSet cookies)
{
    var json = System.Text.Encoding.UTF8.GetBytes(cookies.Serialize());
    var encrypted = Encrypt(json);
    var dir = Path.GetDirectoryName(_filePath);
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);
    await File.WriteAllBytesAsync(_filePath, encrypted);
}

public async Task<GvCookieSet?> LoadAsync()
{
    if (!File.Exists(_filePath))
        return null;
    try
    {
        var encrypted = await File.ReadAllBytesAsync(_filePath);
        var json = System.Text.Encoding.UTF8.GetString(Decrypt(encrypted));
        return GvCookieSet.Deserialize(json);
    }
    catch (CryptographicException) { return null; }
    catch (System.Text.Json.JsonException) { return null; }
}
```

- [ ] **Step 4: Update GvHttpClientHandler to use GvCookieSet**

In `src/RotaryPhoneController.GVBridge/Auth/GvHttpClientHandler.cs`:

Change the cookie getter from `Func<Task<GvCookieJar>>` to `Func<Task<GvCookieSet>>`:

```csharp
private readonly Func<Task<GvCookieSet>> _getCookies;

public GvHttpClientHandler(Func<Task<GvCookieSet>> getCookies, HttpMessageHandler inner)
    : base(inner) { _getCookies = getCookies; }

public GvHttpClientHandler(Func<Task<GvCookieSet>> getCookies)
    { _getCookies = getCookies; }

protected override async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request, CancellationToken cancellationToken)
{
    var cookies = await _getCookies();
    var hash = GvSapisidHash.ComputeCurrent(cookies.Sapisid);

    request.Headers.Authorization = new AuthenticationHeaderValue("SAPISIDHASH", hash);
    request.Headers.TryAddWithoutValidation("Cookie", cookies.ToCookieHeader());
    request.Headers.TryAddWithoutValidation("Origin", "https://voice.google.com");
    request.Headers.TryAddWithoutValidation("Referer", "https://voice.google.com/");
    request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");

    return await base.SendAsync(request, cancellationToken);
}
```

- [ ] **Step 5: Build**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`

Fix any compile errors from the GvCookieJar → GvCookieSet transition.

- [ ] **Step 6: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Auth/
git commit -m "feat(auth): port cookie management from GVResearch

Replace GvCookieJar with GvCookieSet (raw header capture).
Add CookieRetriever (Chrome CDP extraction).
Update GvCookieStore and GvHttpClientHandler for new cookie model.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: AudioResampler 48kHz ↔ 8kHz

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/AudioResamplerTests.cs`

- [ ] **Step 1: Write failing tests**

Add to `src/RotaryPhoneController.GVBridge.Tests/AudioResamplerTests.cs`:

```csharp
[Fact]
public void Resample48kTo8k_ReducesSampleCountBySix()
{
    // 48 samples at 48kHz = 1ms, should produce 8 samples at 8kHz
    var pcm48k = new byte[48 * 2]; // 48 samples * 2 bytes each
    for (int i = 0; i < 48; i++)
    {
        var sample = (short)(i * 100);
        pcm48k[i * 2] = (byte)(sample & 0xFF);
        pcm48k[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
    }

    var result = AudioResampler.Resample48kTo8k(pcm48k);
    Assert.Equal(8 * 2, result.Length); // 8 samples * 2 bytes
}

[Fact]
public void Resample8kTo48k_IncreasesSampleCountBySix()
{
    var pcm8k = new byte[8 * 2]; // 8 samples
    for (int i = 0; i < 8; i++)
    {
        var sample = (short)(i * 1000);
        pcm8k[i * 2] = (byte)(sample & 0xFF);
        pcm8k[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
    }

    var result = AudioResampler.Resample8kTo48k(pcm8k);
    Assert.Equal(48 * 2, result.Length); // 48 samples * 2 bytes
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "Resample48k" -v n`
Expected: FAIL — methods don't exist

- [ ] **Step 3: Implement 48k↔8k resampling**

Add to `src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs`:

```csharp
/// <summary>
/// Downsample 48kHz PCM to 8kHz (6:1 ratio). Averages groups of 6 samples.
/// </summary>
public static byte[] Resample48kTo8k(byte[] pcm48k)
{
    int sampleCount = pcm48k.Length / 2;
    int outCount = sampleCount / 6;
    var result = new byte[outCount * 2];

    for (int i = 0; i < outCount; i++)
    {
        long sum = 0;
        for (int j = 0; j < 6; j++)
        {
            int idx = (i * 6 + j) * 2;
            if (idx + 1 < pcm48k.Length)
                sum += (short)(pcm48k[idx] | (pcm48k[idx + 1] << 8));
        }
        short avg = (short)(sum / 6);
        result[i * 2] = (byte)(avg & 0xFF);
        result[i * 2 + 1] = (byte)((avg >> 8) & 0xFF);
    }

    return result;
}

/// <summary>
/// Upsample 8kHz PCM to 48kHz (1:6 ratio). Linear interpolation between samples.
/// </summary>
public static byte[] Resample8kTo48k(byte[] pcm8k)
{
    int sampleCount = pcm8k.Length / 2;
    int outCount = sampleCount * 6;
    var result = new byte[outCount * 2];

    for (int i = 0; i < sampleCount; i++)
    {
        short current = (short)(pcm8k[i * 2] | (pcm8k[i * 2 + 1] << 8));
        short next = (i + 1 < sampleCount)
            ? (short)(pcm8k[(i + 1) * 2] | (pcm8k[(i + 1) * 2 + 1] << 8))
            : current;

        for (int j = 0; j < 6; j++)
        {
            // Linear interpolation: current + (next - current) * j / 6
            short sample = (short)(current + (next - current) * j / 6);
            int outIdx = (i * 6 + j) * 2;
            result[outIdx] = (byte)(sample & 0xFF);
            result[outIdx + 1] = (byte)((sample >> 8) & 0xFF);
        }
    }

    return result;
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "Resample" -v n`
Expected: All pass (including existing 16k↔8k tests)

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Audio/AudioResampler.cs \
        src/RotaryPhoneController.GVBridge.Tests/AudioResamplerTests.cs
git commit -m "feat(audio): add 48kHz↔8kHz resampling for Opus↔G.711 bridge

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Rewrite GVAudioBridgeService

Replace Chrome extension audio source with SIP transport audio events.

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Services/GVAudioBridgeService.cs`

- [ ] **Step 1: Rewrite the audio bridge**

The bridge no longer depends on `GVBridgeService` (the Chrome extension WebSocket server). Instead, it receives audio from `GvSipTransport.AudioReceived` events and sends audio back via `GvSipTransport.SendAudio()`.

Replace the constructor to take the SIP transport instead of bridge service:

```csharp
public class GVAudioBridgeService : IDisposable
{
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GVAudioBridgeService> _logger;

    private RTPSession? _rtpSession;
    private CancellationTokenSource? _cts;
    private GvSipTransport? _sipTransport;
    private string? _activeCallId;

    public bool IsActive { get; private set; }
    public AudioBridgeStats Stats { get; } = new();

    public GVAudioBridgeService(
        IOptions<GVBridgeConfig> config,
        ILogger<GVAudioBridgeService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Set the SIP transport and call ID for the current call.
    /// Called by GVApiAdapter when a call is answered.
    /// </summary>
    public void SetSipTransport(GvSipTransport sipTransport, string callId)
    {
        _sipTransport = sipTransport;
        _activeCallId = callId;
    }
```

Replace `StartAsync` to subscribe to SIP audio and wire outbound audio:

```csharp
    public async Task StartAsync()
    {
        if (IsActive) return;

        // Create RTP session to HT801 (same as before)
        _rtpSession = new RTPSession(false, false, false);
        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio,
            false,
            new List<SDPAudioVideoMediaFormat>
            {
                new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU)
            });
        _rtpSession.addTrack(audioTrack);

        var destEp = new IPEndPoint(IPAddress.Parse(_config.HT801Ip), _config.HT801RtpPort);
        _rtpSession.SetDestination(SDPMediaTypesEnum.audio, destEp,
            new IPEndPoint(destEp.Address, destEp.Port + 1));
        _rtpSession.AcceptRtpFromAny = true;

        // HT801 → Google: receive G.711 from HT801, resample to 48kHz, send to SIP transport
        _rtpSession.OnRtpPacketReceived += OnHt801RtpReceived;

        // Google → HT801: receive 48kHz PCM from SIP transport, resample to 8kHz G.711, send to HT801
        if (_sipTransport != null)
            _sipTransport.AudioReceived += OnSipAudioReceived;

        await _rtpSession.Start();
        _cts = new CancellationTokenSource();
        IsActive = true;
        _logger.LogInformation("Audio bridge started (SIP transport ↔ HT801 RTP)");
    }
```

Replace the audio handlers:

```csharp
    /// <summary>
    /// Google → HT801: Opus decoded 48kHz PCM → resample to 8kHz → G.711 µ-law → RTP
    /// </summary>
    private void OnSipAudioReceived(object? sender, AudioDataEventArgs e)
    {
        if (_rtpSession == null || !IsActive) return;
        try
        {
            var pcm48k = e.PcmData.ToArray();
            var pcm8k = AudioResampler.Resample48kTo8k(pcm48k);
            var mulaw = MuLawEncoder.Encode(pcm8k);
            _rtpSession.SendAudio(160, mulaw); // 160 samples @ 8kHz = 20ms
            Stats.RecordInboundSent();
        }
        catch (Exception ex)
        {
            Stats.RecordInboundError();
            _logger.LogWarning(ex, "Error in SIP→HT801 audio path");
        }
    }

    /// <summary>
    /// HT801 → Google: G.711 µ-law → decode → resample 8kHz to 48kHz → send to SIP transport
    /// </summary>
    private void OnHt801RtpReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
    {
        if (_sipTransport == null || _activeCallId == null || !IsActive) return;
        try
        {
            var mulaw = rtpPacket.Payload;
            var pcm8k = MuLawEncoder.Decode(mulaw);
            var pcm48k = AudioResampler.Resample8kTo48k(pcm8k);
            _sipTransport.SendAudio(_activeCallId, pcm48k, 48000);
            Stats.RecordOutboundReceived();
        }
        catch (Exception ex)
        {
            Stats.RecordOutboundError();
            _logger.LogWarning(ex, "Error in HT801→SIP audio path");
        }
    }
```

Update `StopAsync`:

```csharp
    public async Task StopAsync()
    {
        if (!IsActive) return;

        _cts?.Cancel();

        if (_sipTransport != null)
            _sipTransport.AudioReceived -= OnSipAudioReceived;

        if (_rtpSession != null)
        {
            _rtpSession.OnRtpPacketReceived -= OnHt801RtpReceived;
            _rtpSession.Close("bridge stopped");
            _rtpSession.Dispose();
            _rtpSession = null;
        }

        _sipTransport = null;
        _activeCallId = null;
        IsActive = false;
        _logger.LogInformation("Audio bridge stopped");
    }
```

Keep `MuLawEncoder` and `AudioBridgeStats` classes unchanged at the bottom of the file.

- [ ] **Step 2: Build**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Services/GVAudioBridgeService.cs
git commit -m "refactor(audio): replace Chrome extension audio with SIP transport

Inbound: Opus 48kHz PCM → resample → G.711 µ-law → HT801 RTP
Outbound: HT801 RTP → G.711 → resample → 48kHz PCM → Opus via SIP

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Rewrite GVApiAdapter

Replace extension + HTTP API with SIP transport for all call operations.

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

- [ ] **Step 1: Rewrite the adapter**

Major changes:
- Remove all extension dependencies (`_bridgeService`, `AnswerMessage`, `MuteTabMessage`, etc.)
- Remove `GvCallClient`, `GvSignalerClient` fields
- Add `GvSipTransport` field
- `ActivateAsync`: create credential provider + SIP transport, subscribe to incoming calls
- `PlaceCallAsync`: SIP INVITE via `_sipTransport.InitiateAsync()`
- `HangUpAsync`: SIP BYE via `_sipTransport.HangupAsync()`
- `OnCallAnsweredOnRotaryPhoneAsync`: wire audio bridge to SIP transport, start bridge
- `OnCallHungUpAsync`: stop audio bridge, SIP BYE

The adapter no longer needs `SetServices(GVBridgeService, ...)` since GVBridgeService is gone. The audio bridge is still injected but via constructor or `SetAudioBridge()`.

Read the current GVApiAdapter fully, then rewrite keeping the same `ICallAdapter` interface contract. The cookie auth, health check, and availability logic remain. The call control and audio path change completely.

Key code patterns for the rewrite:

```csharp
// In ActivateAsync, after health check:
var credProvider = new GvSipCredentialProvider(_gvHttp, _config.GvApiBaseUrl, _config.GvApiKey,
    _loggerFactory.CreateLogger<GvSipCredentialProvider>());
_sipTransport = new GvSipTransport(
    _loggerFactory.CreateLogger<GvSipTransport>(),
    () => credProvider.GetCredentialsAsync(),
    _loggerFactory);
_sipTransport.IncomingCallReceived += HandleSipIncomingCall;

// In PlaceCallAsync:
var result = await _sipTransport!.InitiateAsync(e164Number, ct);
if (!result.Success)
    throw new InvalidOperationException($"SIP INVITE failed: {result.ErrorMessage}");
Interlocked.Exchange(ref _activeCallId, result.CallId);
return result.CallId;

// In OnCallAnsweredOnRotaryPhoneAsync:
_audioBridge!.SetSipTransport(_sipTransport!, _activeCallId!);
await _audioBridge.StartAsync();

// In OnCallHungUpAsync:
await _audioBridge!.StopAsync();
var callId = Interlocked.Exchange(ref _activeCallId, null);
if (callId != null)
    await _sipTransport!.HangupAsync(callId);
```

For incoming calls:
```csharp
private void HandleSipIncomingCall(object? sender, IncomingCallEventArgs e)
{
    Interlocked.Exchange(ref _activeCallId, e.CallInfo.CallId);
    _logger.LogInformation("SIP incoming call from {Number}", e.CallInfo.CallerNumber);
    OnIncomingCall?.Invoke(e.CallInfo.CallerNumber);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs
git commit -m "feat(sip): rewrite GVApiAdapter to use SIP INVITE/BYE

Replace Chrome extension commands and HTTP API with direct SIP
signaling. Incoming calls detected via SIP INVITE reception.
Audio bridged between SIP transport (48kHz Opus) and HT801 (8kHz G.711).

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Update DI Registration and Config

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`

- [ ] **Step 1: Update GVBridgeConfig**

Remove extension-related config, keep RTP + GV API config:

```csharp
public class GVBridgeConfig
{
    // RTP audio bridging to HT801
    public int LocalRtpPort { get; set; } = 5070;
    public string LocalIp { get; set; } = "0.0.0.0";
    public string HT801Ip { get; set; } = "192.168.86.22";
    public int HT801RtpPort { get; set; } = 5004;

    // Google Voice API
    public string GvApiBaseUrl { get; set; } = "https://clients6.google.com/voice/v1/voiceclient";
    public string GvApiKey { get; set; } = "AIzaSyDTYc1N4xiODyrQYK0Kl6g_y279LjYkrBg";

    // Cookie management
    public string CookieFilePath { get; set; } = "data/gv-cookies.enc";
    public string CookieKeyFilePath { get; set; } = "data/gv-key.bin";
    public int CookieRefreshIntervalMinutes { get; set; } = 5;

    // Call adapter
    public string DefaultMode { get; set; } = "GVApi";
    public string CallLogDbPath { get; set; } = "data/gvbridge-calllog.db";
}
```

- [ ] **Step 2: Rewrite DI registration**

Remove `GVBridgeService`, `GVBridgeHub`, `GVBridgeEventBridge`, `GVSmsService` registrations.
Keep `GVAudioBridgeService` and `GVApiAdapter`.

```csharp
public static IServiceCollection AddGVBridge(
    this IServiceCollection services,
    IConfiguration configuration)
{
    services.Configure<GVBridgeConfig>(configuration.GetSection("GVBridge"));

    services.AddSingleton<GVAudioBridgeService>();
    services.AddSingleton<GVApiAdapter>();
    services.AddSingleton<ICallAdapter>(sp => sp.GetRequiredService<GVApiAdapter>());

    // Wire audio bridge into adapter
    services.AddHostedService(sp =>
    {
        var adapter = sp.GetRequiredService<GVApiAdapter>();
        var audioBridge = sp.GetRequiredService<GVAudioBridgeService>();
        adapter.SetAudioBridge(audioBridge);
        return new GvApiAdapterSetup();
    });

    return services;
}

// Remove MapGVBridge or simplify (no SignalR hub needed)
public static IEndpointRouteBuilder MapGVBridge(this IEndpointRouteBuilder endpoints)
{
    endpoints.MapControllers();
    return endpoints;
}

private class GvApiAdapterSetup : IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Extensions/ \
        src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs
git commit -m "refactor(di): simplify DI registration, remove Chrome extension services

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Update gv-login CLI

**Files:**
- Modify: `src/RotaryPhoneController.Server/Program.cs`

- [ ] **Step 1: Replace GvLoginTool with CookieRetriever**

In `Program.cs`, replace the `gv-login` handler:

```csharp
if (args.Contains("gv-login"))
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var logger = loggerFactory.CreateLogger("GvLogin");

    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .Build();
    var gvConfig = config.GetSection("GVBridge");

    var cookiePath = gvConfig["CookieFilePath"] ?? "data/gv-cookies.enc";
    var keyPath = gvConfig["CookieKeyFilePath"] ?? "data/gv-key.bin";

    var result = await RotaryPhoneController.GVBridge.Auth.CookieRetriever.RetrieveAndSaveAsync(
        cookiePath, keyPath,
        msg => logger.LogInformation("{Message}", msg));

    if (result)
        logger.LogInformation("Cookie extraction successful. Start the server normally.");
    else
        logger.LogError("Cookie extraction failed. Check Chrome is running and you're logged into voice.google.com.");

    return;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Server/Program.cs
git commit -m "feat(cli): update gv-login to use CookieRetriever (Chrome CDP)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Delete Obsolete Code

**Files:**
- Delete: `src/RotaryPhoneController.GVBridge/Services/GVBridgeService.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Models/ExtensionMessage.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Api/GVBridgeHub.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Auth/GvCookieJar.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Auth/GvCookieRotationService.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Tools/GvLoginTool.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Clients/GvCallClient.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Signaler/GvSignalerClient.cs`
- Delete: `src/RotaryPhoneController.GVBridge/Signaler/SignalerEvent.cs`
- Delete: `ChromeExtension/` (entire directory)

- [ ] **Step 1: Delete files**

```bash
rm src/RotaryPhoneController.GVBridge/Services/GVBridgeService.cs
rm src/RotaryPhoneController.GVBridge/Models/ExtensionMessage.cs
rm -f src/RotaryPhoneController.GVBridge/Api/GVBridgeHub.cs
rm src/RotaryPhoneController.GVBridge/Auth/GvCookieJar.cs
rm src/RotaryPhoneController.GVBridge/Auth/GvCookieRotationService.cs
rm src/RotaryPhoneController.GVBridge/Tools/GvLoginTool.cs
rm src/RotaryPhoneController.GVBridge/Clients/GvCallClient.cs
rm src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs
rm src/RotaryPhoneController.GVBridge/Signaler/GvSignalerClient.cs
rm src/RotaryPhoneController.GVBridge/Signaler/SignalerEvent.cs
rm -rf ChromeExtension/
```

- [ ] **Step 2: Remove Playwright package reference**

In `src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`, CookieRetriever still uses Playwright. Keep the reference if it's used, or remove if CookieRetriever uses Playwright from its own dependency.

Check: CookieRetriever imports `Microsoft.Playwright` — so keep the Playwright package reference.

- [ ] **Step 3: Fix all remaining compile errors**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`

Fix any references to deleted types:
- `GVBridgeService` references in other files
- `ExtensionMessage` references
- `GVBridgeHub` references in `MapGVBridge`
- `GvCookieJar` references (should be replaced by `GvCookieSet`)
- Test files referencing deleted classes

- [ ] **Step 4: Update tests**

Delete or update test files that reference removed classes:
- `GVBrowserAdapterTests.cs` (already deleted in prior migration)
- `GvCookieStoreTests.cs` — update to use `GvCookieSet` instead of `GvCookieJar`
- `GvHttpClientHandlerTests.cs` — update to use `GvCookieSet`
- `GvCallClientTests.cs` — delete (GvCallClient removed)
- `GVApiAdapterTests.cs` — update for new constructor/API

- [ ] **Step 5: Build and run all tests**

Run: `dotnet build && dotnet test -v n`
Expected: Build succeeds, all tests pass

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: remove Chrome extension, signaler, and obsolete services

Delete GVBridgeService (WebSocket server), ExtensionMessage models,
GVBridgeHub (SignalR), GvCallClient, GvSmsClient, GvSignalerClient,
GvCookieJar, GvCookieRotationService, GvLoginTool, and Chrome extension.
All replaced by SIP-over-WebSocket transport and CookieRetriever.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Integration Smoke Test

- [ ] **Step 1: Build entire solution**

Run: `dotnet build`
Expected: 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 3: Verify no Chrome extension references remain**

```bash
grep -r "ChromeExtension\|GVBridgeService\|ExtensionMessage\|GvBridgeHub\|tabCapture" src/ --include="*.cs" -l
```

Expected: No matches

- [ ] **Step 4: Verify no old SIPSorcery references**

```bash
grep -r "8\.0\.23" src/ --include="*.csproj"
```

Expected: No matches (all should be 10.0.6-diag)

- [ ] **Step 5: Start server and verify initialization**

Run: `dotnet run --project src/RotaryPhoneController.Server --framework net10.0 -- 2>&1 | head -30`

Expected: Server starts, logs show "GVApiAdapter activating", no crashes. Will fail to activate if no cookies are present (expected).

---

## Summary

| Task | Component | Complexity |
|------|-----------|-----------|
| 1 | Package setup (SIPSorcery upgrade, Concentus, nuget.config) | Low |
| 2 | Copy SIP transport files (3 files + models) | Medium |
| 3 | Copy cookie management files (CookieSet, Retriever) | Medium |
| 4 | AudioResampler 48k↔8k | Low |
| 5 | Rewrite GVAudioBridgeService | Medium |
| 6 | Rewrite GVApiAdapter | High |
| 7 | Update DI + Config | Medium |
| 8 | Update gv-login CLI | Low |
| 9 | Delete obsolete code + fix tests | Medium |
| 10 | Smoke test | Low |

**Critical path:** 1 → 2 → 4 → 5 → 6 → 7 → 9 → 10. Task 3 can parallel with 2. Task 8 can parallel with 5-7.

**Highest risk:** Task 2 (namespace/type adjustments in 51KB file) and Task 6 (adapter rewrite touching all call flows).
