using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace RotaryPhoneController.GVBridge.Sip;

/// <summary>
/// SIP-over-WebSocket transport (RFC 7118) to Google Voice's SIP proxy.
///
/// Call flow (from captured traffic):
///   1. sipregisterinfo/get -> Bearer token + SIP identity
///   2. WSS connect to 216.239.36.145:443 (subprotocol "sip")
///   3. SIP REGISTER with Bearer auth
///   4. SIP INVITE sip:+{phone}@web.c.pbx.voice.sip.google.com
///   5. 183 Session Progress (SDP answer, early media)
///   6. PRACK -> 200 OK
///   7. 180 Ringing -> PRACK -> 200 OK
///   8. 200 OK (INVITE) -> ACK -> audio flows
///   9. BYE to hangup
/// </summary>
public sealed class GvSipTransport : IAsyncDisposable
{
    private const string SipDomain = "web.c.pbx.voice.sip.google.com";
    private const string WssUrl = "wss://web.voice.telephony.goog/websocket";
    private const string UserAgent = "GoogleVoice voice.web-frontend_20260318.08_p1";

    private static readonly Action<ILogger, Exception?> LogRegistering =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, "SipRegistering"),
            "SIP REGISTER to Google Voice...");

    private static readonly Action<ILogger, Exception?> LogRegistered =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, "SipRegistered"),
            "SIP registration successful");

    private static readonly Action<ILogger, string, string, Exception?> LogInviting =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(3, "SipInviting"),
            "SIP INVITE {CallId} to {ToNumber}");

    private static readonly Action<ILogger, string, Exception?> LogCallEnded =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(6, "SipEnded"),
            "SIP call {CallId} ended");

    private static readonly Action<ILogger, string, Exception?> LogError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(7, "SipError"),
            "SIP error: {Message}");

    private readonly ILogger<GvSipTransport> _logger;
    private readonly Func<Task<SipCredentials>> _getCredentials;
    private readonly ConcurrentDictionary<string, SipCallSession> _activeCalls = new();
    private readonly Func<Uri, ILogger, ISipWebSocketChannel> _channelFactory;
    private readonly ReconnectOptions _options;
    private readonly TimeProvider _timeProvider;

    // Cancelled in DisposeAsync so the keep-alive timer + reconnect loop stop on shutdown.
    private readonly CancellationTokenSource _lifetimeCts = new();

    private ISipWebSocketChannel? _wsChannel;
    private SipCredentials? _credentials;
    private string? _serviceRoute;
    private string? _regContactUser;
    private string? _regWsHost;
    private bool _registered;
    private bool _disposed;

    // Negotiated keep-alive frequency (RFC 6223 keep= from the REGISTER 200-OK Via).
    private int _keepAliveIntervalSeconds;
    private ITimer? _keepAliveTimer;

    // Single-flight reconnect guard (0 = idle, 1 = reconnect in flight).
    private int _reconnecting;

    // Handlers currently subscribed to _wsChannel — captured so they can be unsubscribed
    // before the old channel is disposed on reconnect (fixes the latent handler/channel leak).
    private EventHandler<SipMessageEventArgs>? _currentMessageHandler;
    private EventHandler<WebSocketClosedEventArgs>? _currentClosedHandler;

    /// <summary>
    /// Whether SIP registration with Google Voice is currently active AND the underlying
    /// socket is open. A dead socket can never report registered-true (honest status).
    /// </summary>
    public bool IsRegistered => _registered && IsConnected;

    /// <summary>Whether the underlying SIP WebSocket is currently connected.</summary>
    public bool IsConnected => _wsChannel?.IsConnected ?? false;

    /// <summary>UTC timestamp of the most recent successful REGISTER 200-OK, if any.</summary>
    public DateTime? LastConnectedAt { get; private set; }

    public event EventHandler<IncomingCallEventArgs>? IncomingCallReceived;

    public event EventHandler<AudioDataEventArgs>? AudioReceived;

    public event EventHandler<CallStatusChangedEventArgs>? CallStatusChanged;

    /// <summary>
    /// Raised when REGISTER is genuinely rejected after the Digest re-send, or when the
    /// credential fetch returns HTTP 401/403. The adapter subscribes to escalate to a
    /// cookie refresh (RotateCookies primary, CDP fallback). NOT raised on plain network drops.
    /// </summary>
    public event EventHandler<AuthenticationFailedEventArgs>? AuthenticationFailed;

    /// <param name="logger">Logger</param>
    /// <param name="getCredentials">Async factory that calls sipregisterinfo/get and returns credentials</param>
    /// <param name="loggerFactory">Optional ILoggerFactory to route SIPSorcery internal logs through</param>
    public GvSipTransport(
        ILogger<GvSipTransport> logger,
        Func<Task<SipCredentials>> getCredentials,
        ILoggerFactory? loggerFactory = null)
        : this(logger, getCredentials, loggerFactory, channelFactory: null, options: null, timeProvider: null)
    {
    }

    /// <summary>
    /// Test/extensibility constructor. Allows injecting a fake channel factory, tuned
    /// reconnect/keep-alive options, and a TimeProvider so timing-sensitive paths are
    /// deterministic. Production code uses the public 3-arg overload, which defaults all three.
    /// </summary>
    internal GvSipTransport(
        ILogger<GvSipTransport> logger,
        Func<Task<SipCredentials>> getCredentials,
        ILoggerFactory? loggerFactory,
        Func<Uri, ILogger, ISipWebSocketChannel>? channelFactory,
        ReconnectOptions? options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(getCredentials);
        _logger = logger;
        _getCredentials = getCredentials;
        _options = options ?? ReconnectOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channelFactory = channelFactory
            ?? ((uri, log) => new GvSipWebSocketChannel(
                uri, log, TimeSpan.FromSeconds(_options.DefaultKeepAliveSeconds)));

        // Route SIPSorcery internal logs (including DTLS diagnostics) through our logging pipeline
        if (loggerFactory is not null)
            SIPSorcery.LogFactory.Set(loggerFactory);
    }

    /// <summary>
    /// Register with Google Voice SIP infrastructure. Must be called before
    /// placing or receiving calls. Safe to call multiple times (no-op if already registered).
    /// </summary>
    public async Task EnsureRegisteredAsync(CancellationToken ct = default)
    {
        if (!_registered || _wsChannel is null || !_wsChannel.IsConnected)
        {
            _logger.LogInformation("Re-registering: registered={Registered}, wsConnected={WsConnected}",
                _registered, _wsChannel?.IsConnected ?? false);
            _registered = false;  // Force full re-registration
            await RegisterAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<TransportCallResult> InitiateAsync(string toNumber, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toNumber);

        // Ensure registered
        await EnsureRegisteredAsync(ct).ConfigureAwait(false);

        var callId = Guid.NewGuid().ToString("D").ToUpperInvariant();

        // Format phone number
        var destNumber = toNumber.StartsWith('+') ? toNumber : $"+1{toNumber}";

        LogInviting(_logger, callId, destNumber, null);

        if (_wsChannel is null || _credentials is null)
            return new TransportCallResult(callId, false, "Not registered");

        try
        {
            // Create RTCPeerConnection for DTLS-SRTP (required by Google)
            var pc = new SIPSorcery.Net.RTCPeerConnection(new SIPSorcery.Net.RTCConfiguration
            {
                iceServers = [new SIPSorcery.Net.RTCIceServer { urls = "stun:stun.l.google.com:19302" }],
                // Google's DTLS relay uses TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 (confirmed via Chrome WebRTC stats)
                X_UseRsaForDtlsCertificate = true,
                X_UseRtpFeedbackProfile = true, // Use SAVPF profile like Chrome
            });

            var audioTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.audio, false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new(new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1")),
                    new(SDPWellKnownMediaFormatsEnum.G722),
                    new(SDPWellKnownMediaFormatsEnum.PCMU),
                    new(SDPWellKnownMediaFormatsEnum.PCMA),
                });
            pc.addTrack(audioTrack);

            // Wire audio receive events — decode Opus to PCM before firing event
            // Google sends Opus at 48kHz stereo (OPUS/48000/2) — decode to mono for playback
            var rtpCount = 0;
            const int opusChannels = 2; // stereo from Google
            const int maxFrameSamples = 960 * opusChannels; // 20ms at 48kHz stereo
            var opusDecoder = Concentus.OpusCodecFactory.CreateDecoder(48000, opusChannels);
            pc.OnRtpPacketReceived += (ep, mt, pkt) =>
            {
                // Allocate per-invocation to avoid cross-thread sharing (~23KB, acceptable)
                var pcmBuf = new short[maxFrameSamples * 6]; // room for up to 120ms frames
                rtpCount++;
                if (rtpCount <= 5 || rtpCount % 500 == 0)
                {
#pragma warning disable CA1848, CA1873
                    _logger.LogInformation(
                        "RTP #{Count} type={MediaType} pt={PayloadType} len={Len} ssrc={Ssrc} seq={Seq}",
                        rtpCount, mt, pkt.Header.PayloadType, pkt.Payload.Length,
                        pkt.Header.SyncSource, pkt.Header.SequenceNumber);
#pragma warning restore CA1848, CA1873
                }

                if (mt == SDPMediaTypesEnum.audio && pkt.Header.PayloadType == 111)
                {
                    // Decode Opus -> 16-bit PCM at 48kHz
#pragma warning disable CA1031
                    try
                    {
                        var samples = opusDecoder.Decode(pkt.Payload, pcmBuf.AsSpan(), 960);
                        if (samples > 0)
                        {
                            // Downmix stereo -> mono: average L+R channels
                            var monoSamples = samples; // samples is per-channel
                            var monoBytes = new byte[monoSamples * 2];
                            for (int i = 0; i < monoSamples; i++)
                            {
                                int left = pcmBuf[i * opusChannels];
                                int right = pcmBuf[i * opusChannels + 1];
                                short mono = (short)((left + right) / 2);
                                monoBytes[i * 2] = (byte)(mono & 0xFF);
                                monoBytes[i * 2 + 1] = (byte)(mono >> 8);
                            }

                            AudioReceived?.Invoke(this, new AudioDataEventArgs(callId, monoBytes, 48000));
                        }
                    }
                    catch (Exception ex)
                    {
                        if (rtpCount <= 3)
                        {
#pragma warning disable CA1848, CA1873
                            _logger.LogWarning(ex, "Opus decode failed for packet #{Count}", rtpCount);
#pragma warning restore CA1848, CA1873
                        }
                    }
#pragma warning restore CA1031
                }
            };

            // Log ALL state changes for DTLS debugging
            pc.onconnectionstatechange += (state) =>
            {
#pragma warning disable CA1848, CA1873
                _logger.LogInformation("Call {CallId} connection: {State}", callId, state);
#pragma warning restore CA1848, CA1873
            };
            pc.oniceconnectionstatechange += (state) =>
            {
#pragma warning disable CA1848, CA1873
                _logger.LogInformation("Call {CallId} ICE: {State}", callId, state);

#if DEBUG
                // Diagnostic: dump DTLS configuration via reflection when ICE connects
                if (state == SIPSorcery.Net.RTCIceConnectionState.connected)
                {
                    try
                    {
                        var configField = pc.GetType().GetField("_configuration",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (configField?.GetValue(pc) is SIPSorcery.Net.RTCConfiguration cfg)
                        {
                            _logger.LogInformation(
                                "DTLS config: UseRsa={UseRsa}, DisableEMS={DisableEms}, FeedbackProfile={Fb}",
                                cfg.X_UseRsaForDtlsCertificate, cfg.X_DisableExtendedMasterSecretKey,
                                cfg.X_UseRtpFeedbackProfile);
                        }

                        _logger.LogInformation("IceRole={IceRole}", pc.IceRole);
                    }
#pragma warning disable CA1031
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to inspect DTLS config");
                    }
#pragma warning restore CA1031
                }
#endif
#pragma warning restore CA1848, CA1873
            };
            pc.onsignalingstatechange += () =>
            {
#pragma warning disable CA1848, CA1873
                _logger.LogInformation("Call {CallId} signaling: {State}", callId, pc.signalingState);
#pragma warning restore CA1848, CA1873
            };

            // Create Opus encoder/decoder — lifetimes managed by SipCallSession.Dispose
#pragma warning disable CA2000
            var opusEncoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
#pragma warning restore CA2000

            // Store session with peer connection, encoder, and decoder
            var session = new SipCallSession(callId) { PeerConnection = pc, OpusEncoder = opusEncoder, OpusDecoder = opusDecoder };
            _activeCalls[callId] = session;

            var offer = pc.createOffer();
            await pc.setLocalDescription(offer).ConfigureAwait(false);

            // Use a random CSeq like the browser does — stored per-call to avoid corruption with overlapping calls
            session.InviteCSeq = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000, 9999);
            session.PrackCSeq = session.InviteCSeq + 1;

            var invTag = CallProperties.CreateNewTag();
            var sipUsernameEncoded = Uri.EscapeDataString(_credentials.SipUsername);

            var inviteMsg =
                $"INVITE sip:{destNumber}@{SipDomain} SIP/2.0\r\n" +
                $"Via: SIP/2.0/wss {_regWsHost};branch={CallProperties.CreateBranchId()};keep\r\n" +
                $"From: <sip:{sipUsernameEncoded}@{SipDomain}>;tag={invTag}\r\n" +
                $"To: <sip:{destNumber}@{SipDomain}>\r\n" +
                $"Call-ID: {callId}\r\n" +
                $"CSeq: {session.InviteCSeq} INVITE\r\n" +
                $"Contact: <sip:{_regContactUser}@{_regWsHost};transport=wss>\r\n" +
                $"Content-Type: application/sdp\r\n" +
                $"Session-Expires: 90\r\n" +
                $"Supported: timer,100rel,ice,replaces,outbound,record-aware\r\n" +
                $"User-Agent: {UserAgent}\r\n" +
                $"X-Google-Client-Info: Ci1Hb29nbGVWb2ljZSB2b2ljZS53ZWItZnJvbnRlbmRfMjAyNjAzMTguMDhfcDESLUdvb2dsZVZvaWNlIHZvaWNlLndlYi1mcm9udGVuZF8yMDI2MDMxOC4wOF9wMRgFKhBDaHJvbWUgMTQ2LjAuMC4w\r\n" +
                (_serviceRoute is not null ? $"Route: {_serviceRoute}\r\n" : "") +
                $"Max-Forwards: 70\r\n" +
                $"Content-Length: {offer.sdp.Length}\r\n" +
                $"\r\n" +
                offer.sdp;

#pragma warning disable CA1848, CA1873 // Debug/UAT tool — LoggerMessage perf not required
            _logger.LogInformation("Sending SIP INVITE to {Number}, SDP={SdpLen} chars", destNumber, offer.sdp.Length);
#pragma warning restore CA1848, CA1873

            await _wsChannel.SendAsync(inviteMsg, ct).ConfigureAwait(false);

            // The WebSocket receive loop will log the response
            // For now, return success (we'll add proper response handling later)
            return new TransportCallResult(callId, true, null);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogError(_logger, ex.Message, ex);
            return new TransportCallResult(callId, false, ex.Message);
        }
    }

    public Task<TransportCallStatus> GetStatusAsync(string callId, CancellationToken ct = default)
    {
        var status = _activeCalls.TryGetValue(callId, out var session)
            ? session.Status
            : CallStatusType.Unknown;
        return Task.FromResult(new TransportCallStatus(callId, status));
    }

    public async Task HangupAsync(string callId, CancellationToken ct = default)
    {
        LogCallEnded(_logger, callId, null);

        // Use TryGetValue (not TryRemove) to retrieve the session for sending BYE.
        // The incoming BYE handler may have already removed the session from _activeCalls
        // via the CallStatusChanged -> OnCallEnded -> HangUp -> HangupAsync event chain.
        // If the session was already removed, it means Google already sent us a BYE and
        // we responded with 200 OK — no need to send our own BYE.
        if (!_activeCalls.TryGetValue(callId, out var session))
        {
#pragma warning disable CA1848, CA1873
            _logger.LogInformation(
                "HangupAsync: no active session for call {CallId} — " +
                "likely already cleaned up by incoming BYE handler",
                callId);
#pragma warning restore CA1848, CA1873
            return;
        }

        if (_wsChannel is null)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning("HangupAsync: WebSocket channel is null for call {CallId}", callId);
#pragma warning restore CA1848, CA1873
            // Clean up the session even though we can't send BYE
            if (_activeCalls.TryRemove(callId, out _))
            {
                var oldWsStatus = session.Status;
                session.Status = CallStatusType.Completed;
                CallStatusChanged?.Invoke(this, new CallStatusChangedEventArgs(callId, oldWsStatus, CallStatusType.Completed));
                session.Dispose();
            }
            return;
        }

        // AGGRESSIVE MEDIA TEARDOWN: Close the DTLS-SRTP peer connection FIRST.
        // This sends a DTLS close_notify alert to Google immediately, which stops
        // RTP/SRTP flow. Google's RTP timeout detection will then terminate the
        // far-end call within 5-10 seconds. We do this BEFORE the BYE because
        // Google silently ignores our BYE (known interop issue — see KNOWN-ISSUES.md).
        if (session.PeerConnection is not null)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogInformation(
                "Closing DTLS-SRTP peer connection for call {CallId} — " +
                "forces media teardown before BYE",
                callId);
#pragma warning restore CA1848, CA1873
            session.PeerConnection.close();
            session.PeerConnection = null;
        }

        // Send SIP BYE if we have dialog state.
        // NOTE: Google currently ignores our BYE (known issue), but we send it
        // anyway for protocol correctness and in case future debugging resolves
        // the dialog state mismatch.
        if (session.RemoteContactUri is not null && session.ToHeader is not null)
        {
            // CSeq must be higher than any previously sent request in this dialog.
            // PrackCSeq is incremented by session timer re-INVITEs, so it may exceed
            // InviteCSeq. Use the max of both + 1 to guarantee monotonicity.
            var byeCSeq = Math.Max(session.InviteCSeq, session.PrackCSeq) + 1;

            var bye = $"BYE {session.RemoteContactUri} SIP/2.0\r\n";

            foreach (var route in session.RouteSet)
            {
                bye += $"Route: {route}\r\n";
            }

            bye +=
                $"Via: SIP/2.0/wss {_regWsHost};branch={CallProperties.CreateBranchId()};keep\r\n" +
                $"Max-Forwards: 69\r\n" +
                $"To: {session.ToHeader}\r\n" +
                $"From: {session.FromHeader}\r\n" +
                $"Call-ID: {callId}\r\n" +
                $"CSeq: {byeCSeq} BYE\r\n" +
                $"User-Agent: {UserAgent}\r\n" +
                $"Content-Length: 0\r\n" +
                $"\r\n";

#pragma warning disable CA1848, CA1873
            _logger.LogInformation(
                "BYE headers — From: {From}, To: {To}, Call-ID: {CallId}, CSeq: {CSeq}",
                session.FromHeader, session.ToHeader, callId, byeCSeq);
            _logger.LogInformation("Sending SIP BYE to Google Voice for call {CallId}:\n{Bye}",
                callId, bye);
#pragma warning restore CA1848, CA1873

            // Subscribe to all incoming SIP messages during the BYE wait window
            // so we can see if Google sends a response (e.g. 200 OK to BYE)
            EventHandler<SipMessageEventArgs>? debugHandler = null;
            debugHandler = (_, args) =>
            {
#pragma warning disable CA1848, CA1873
                _logger.LogInformation(
                    "SIP message received during BYE wait for {CallId}: {Msg}",
                    callId, args.Message[..Math.Min(500, args.Message.Length)]);
#pragma warning restore CA1848, CA1873
            };
            _wsChannel.MessageReceived += debugHandler;

            try
            {
                await _wsChannel.SendAsync(bye, ct).ConfigureAwait(false);
#pragma warning disable CA1848, CA1873
                _logger.LogInformation("BYE sent successfully via WebSocket for call {CallId}", callId);
#pragma warning restore CA1848, CA1873
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
#pragma warning disable CA1848, CA1873
                _logger.LogError(ex, "Failed to send BYE via WebSocket for call {CallId}", callId);
#pragma warning restore CA1848, CA1873
            }
#pragma warning restore CA1031

            // Brief wait for BYE response (reduced from 2s since media is already torn down).
            // The DTLS close_notify was already sent above, so Google's media timeout is
            // already ticking. This wait is only for diagnostic logging of the BYE response.
#pragma warning disable CA1848, CA1873
            _logger.LogInformation("BYE sent, waiting 500ms for response (media already torn down)...");
#pragma warning restore CA1848, CA1873
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutting down — proceed with cleanup */ }

            _wsChannel.MessageReceived -= debugHandler;
        }
        else
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(
                "Cannot send SIP BYE for call {CallId} — missing dialog state " +
                "(RemoteContactUri={RemoteUri}, ToHeader={To})",
                callId, session.RemoteContactUri ?? "(null)", session.ToHeader ?? "(null)");
#pragma warning restore CA1848, CA1873
        }

        // NOW remove the session after BYE has been sent (or skipped).
        // Use TryRemove to handle the race where incoming BYE handler already removed it.
        if (_activeCalls.TryRemove(callId, out _))
        {
            var oldStatus = session.Status;
            session.Status = CallStatusType.Completed;
            CallStatusChanged?.Invoke(this, new CallStatusChangedEventArgs(callId, oldStatus, CallStatusType.Completed));
            session.Dispose();
        }
        else
        {
#pragma warning disable CA1848, CA1873
            _logger.LogInformation(
                "HangupAsync: session {CallId} already removed (incoming BYE handler won the race) — skipping dispose",
                callId);
#pragma warning restore CA1848, CA1873
        }
    }

    public void SendAudio(string callId, ReadOnlyMemory<byte> pcmData, int sampleRate)
    {
        if (!_activeCalls.TryGetValue(callId, out var session) ||
            session.PeerConnection is null || session.OpusEncoder is null)
            return;

        // Convert byte[] PCM (16-bit LE) to short[] samples
        var span = pcmData.Span;
        var sampleCount = span.Length / 2;
        if (sampleCount == 0) return;

        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8));
        }

        // Encode to Opus — output buffer sized for max Opus frame
        var opusOut = new byte[4000];
#pragma warning disable CA1031
        try
        {
            var encoded = session.OpusEncoder.Encode(
                samples, sampleCount, opusOut, opusOut.Length);
            if (encoded > 0)
            {
                // Duration in RTP timestamp units: sampleCount at 48kHz
                session.PeerConnection.SendAudio(
                    (uint)sampleCount, opusOut.AsSpan(0, encoded).ToArray());
            }
        }
        catch
        {
            // Opus encode failure — skip frame silently
        }
#pragma warning restore CA1031
    }

    private void HandleIncomingInvite(string message)
    {
        var invCallId = ExtractHeaderValue(message, "Call-ID");
        var invTo = ExtractHeader(message, "To");
        var invFrom = ExtractHeader(message, "From");
        var invVias = ExtractAllHeaders(message, "Via");
        var invContact = ExtractHeader(message, "Contact");
        var invCSeq = ExtractHeaderValue(message, "CSeq");
        var recordRoutes = ExtractAllHeaders(message, "Record-Route");

        // Extract caller number from From header: <sip:+1XXXXXXXXXX@domain>
        var callerNumber = "unknown";
        var fromUri = ExtractSipUri(invFrom ?? "");
        if (fromUri.Contains("sip:", StringComparison.Ordinal))
        {
            var userPart = fromUri["sip:".Length..];
            var atIdx = userPart.IndexOf('@', StringComparison.Ordinal);
            if (atIdx > 0)
                callerNumber = userPart[..atIdx];
        }

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Incoming INVITE from {Caller}, Call-ID={CallId}", callerNumber, invCallId);
#pragma warning restore CA1848, CA1873

        if (invCallId is null || invVias.Count == 0 || _wsChannel is null)
            return;

        // Generate a single To-tag for this dialog — must be consistent across 180 and 200
        var dialogTag = CallProperties.CreateNewTag();

        // Send 180 Ringing
        var ringing = "SIP/2.0 180 Ringing\r\n";
        foreach (var via in invVias)
            ringing += $"Via: {via}\r\n";
        ringing +=
            $"To: {invTo};tag={dialogTag}\r\n" +
            $"From: {invFrom}\r\n" +
            $"Call-ID: {invCallId}\r\n" +
            $"CSeq: {invCSeq}\r\n" +
            $"Contact: <sip:{_regContactUser}@{_regWsHost};transport=wss>\r\n" +
            $"User-Agent: {UserAgent}\r\n" +
            $"Content-Length: 0\r\n" +
            $"\r\n";
        _ = SendSipMessageAsync(ringing);

        // Create peer connection for incoming media
        var pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }],
            X_UseRsaForDtlsCertificate = true,
            X_UseRtpFeedbackProfile = true,
        });

        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio, false,
            new List<SDPAudioVideoMediaFormat>
            {
                new(new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1")),
                new(SDPWellKnownMediaFormatsEnum.PCMU),
            });
        pc.addTrack(audioTrack);

        // Set remote SDP from INVITE body
        var sdpSep = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (sdpSep >= 0)
        {
            var sdpBody = message[(sdpSep + 4)..].Trim();
            if (sdpBody.StartsWith("v=", StringComparison.Ordinal))
            {
                pc.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = sdpBody,
                });
            }
        }

        var answer = pc.createAnswer();
        pc.setLocalDescription(answer);

        // Wire audio receive (same as outbound calls)
        var rtpCount = 0;
        const int inOpusChannels = 2;
        var inDecoder = Concentus.OpusCodecFactory.CreateDecoder(48000, inOpusChannels);
        var inEncoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 1,
            Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);

        pc.OnRtpPacketReceived += (ep, mt, pkt) =>
        {
            if (mt != SDPMediaTypesEnum.audio || pkt.Header.PayloadType != 111) return;
            rtpCount++;
            var pcmBuf = new short[960 * inOpusChannels * 6];
#pragma warning disable CA1031
            try
            {
                var samples = inDecoder.Decode(pkt.Payload, pcmBuf.AsSpan(), 960);
                if (samples > 0)
                {
                    var monoBytes = new byte[samples * 2];
                    for (int i = 0; i < samples; i++)
                    {
                        int left = pcmBuf[i * inOpusChannels];
                        int right = pcmBuf[i * inOpusChannels + 1];
                        short mono = (short)((left + right) / 2);
                        monoBytes[i * 2] = (byte)(mono & 0xFF);
                        monoBytes[i * 2 + 1] = (byte)(mono >> 8);
                    }
                    AudioReceived?.Invoke(this, new AudioDataEventArgs(invCallId, monoBytes, 48000));
                }
            }
            catch { /* decode error */ }
#pragma warning restore CA1031
        };

        // Store session — for BYE purposes, swap From/To so that when WE (callee)
        // send a BYE the headers match RFC 3261 section 15.1.1:
        //   From = local party (us, with our dialog tag)
        //   To = remote party (caller, with their tag)
        // The original INVITE has From=caller, To=us, so we reverse for our BYE.
        var ourFromForBye = invTo?.Contains(";tag=", StringComparison.Ordinal) == true
            ? invTo
            : $"{invTo};tag={dialogTag}";

        var inSession = new SipCallSession(invCallId)
        {
            PeerConnection = pc,
            OpusEncoder = inEncoder,
            OpusDecoder = inDecoder,
            RemoteContactUri = ExtractSipUri(invContact ?? ""),
            ToHeader = invFrom,        // Remote party (caller) goes in To for our BYE
            FromHeader = ourFromForBye, // Us (callee + dialog tag) goes in From for our BYE
            RouteSet = [.. recordRoutes],
            Status = CallStatusType.Active,
        };
        inSession.RouteSet.Reverse();
        _activeCalls[invCallId] = inSession;

        // Send 200 OK with SDP answer — same tag as 180
        var ok200 = "SIP/2.0 200 OK\r\n";
        foreach (var via in invVias)
            ok200 += $"Via: {via}\r\n";
        ok200 +=
            $"To: {invTo};tag={dialogTag}\r\n" +
            $"From: {invFrom}\r\n" +
            $"Call-ID: {invCallId}\r\n" +
            $"CSeq: {invCSeq}\r\n" +
            $"Contact: <sip:{_regContactUser}@{_regWsHost};transport=wss>\r\n" +
            $"Supported: timer\r\n" +
            $"Session-Expires: 90;refresher=uac\r\n" +
            $"User-Agent: {UserAgent}\r\n" +
            $"Content-Type: application/sdp\r\n" +
            $"Content-Length: {answer.sdp.Length}\r\n" +
            $"\r\n" +
            answer.sdp;

        _ = SendSipMessageAsync(ok200);

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Answered incoming call from {Caller}, Call-ID={CallId}", callerNumber, invCallId);
#pragma warning restore CA1848, CA1873

        // Fire event to UI
        IncomingCallReceived?.Invoke(this, new IncomingCallEventArgs(
            new IncomingCallInfo(invCallId, callerNumber)));
    }

    private void SendSessionRefresh(string callId)
    {
        if (_wsChannel is null || _credentials is null ||
            !_activeCalls.TryGetValue(callId, out var session) ||
            session.RemoteContactUri is null)
            return;

        var refreshCSeq = Interlocked.Increment(ref session.PrackCSeq);

        var reInvite = $"INVITE {session.RemoteContactUri} SIP/2.0\r\n";

        foreach (var route in session.RouteSet)
        {
            reInvite += $"Route: {route}\r\n";
        }

        reInvite +=
            $"Via: SIP/2.0/wss {_regWsHost};branch={CallProperties.CreateBranchId()};keep\r\n" +
            $"Max-Forwards: 69\r\n" +
            $"To: {session.ToHeader}\r\n" +
            $"From: {session.FromHeader}\r\n" +
            $"Call-ID: {callId}\r\n" +
            $"CSeq: {refreshCSeq} INVITE\r\n" +
            $"Contact: <sip:{_regContactUser}@{_regWsHost};transport=wss>\r\n" +
            $"Session-Expires: 90;refresher=uac\r\n" +
            $"Supported: timer\r\n" +
            $"User-Agent: {UserAgent}\r\n" +
            $"Content-Type: application/sdp\r\n";

        // Re-INVITE needs SDP — use current local description
        var sdp = session.PeerConnection?.localDescription?.sdp?.ToString() ?? "";
        reInvite += $"Content-Length: {sdp.Length}\r\n\r\n{sdp}";

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Sending session refresh re-INVITE for call {CallId}", callId);
#pragma warning restore CA1848, CA1873

        _ = SendSipMessageAsync(reInvite);
    }

    private async Task SendSipMessageAsync(string message, CancellationToken ct = default)
    {
        try
        {
            if (_wsChannel != null)
                await _wsChannel.SendAsync(message, ct);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SIP message");
        }
#pragma warning restore CA1031
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        LogRegistering(_logger, null);

        SipCredentials creds;
        try
        {
            creds = await _getCredentials().ConfigureAwait(false);
        }
        catch (GvAuthException ex)
        {
            // sipregisterinfo/get returned 401/403 — the real stale-cookie failure.
            // Escalate so the adapter can refresh cookies (RotateCookies primary, CDP fallback),
            // then let the reconnect backoff retry. Rethrow so the current attempt counts as failed.
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Credential fetch auth-failed ({Status}) — escalating cookie refresh", ex.StatusCode);
#pragma warning restore CA1848, CA1873
            RaiseAuthenticationFailed($"sipregisterinfo/get {ex.StatusCode}");
            throw;
        }

        // Tear down any prior channel BEFORE creating a new one: unsubscribe the previous
        // handlers and dispose the old channel so reconnects don't leak channels or stack
        // duplicate MessageReceived handlers (which would cause duplicate PRACK/ACK sends).
        await TeardownChannelAsync().ConfigureAwait(false);

        // Connect to Google's SIP proxy via our custom WebSocket channel
        // (bypasses SSL cert name mismatch for raw IP connection)
        var channel = _channelFactory(new Uri(WssUrl), _logger);
        _wsChannel = channel;

        var regTcs = new TaskCompletionSource<bool>();

        // Track PRACKed RSeq values to avoid re-PRACKing retransmissions
        var prackedRSeqs = new HashSet<string>(StringComparer.Ordinal);
        const string XGoogleClientInfo = "X-Google-Client-Info: Ci1Hb29nbGVWb2ljZSB2b2ljZS53ZWItZnJvbnRlbmRfMjAyNjAzMTguMDhfcDESLUdvb2dsZVZvaWNlIHZvaWNlLndlYi1mcm9udGVuZF8yMDI2MDMxOC4wOF9wMRgFKhBDaHJvbWUgMTQ2LjAuMC4w";

        // These are shared between the event handler and the REGISTER below
        var regCallId = Guid.NewGuid().ToString("N")[..22];
        var regTag = CallProperties.CreateNewTag();
        var regWsHost = string.Concat(Guid.NewGuid().ToString("N").AsSpan(0, 12), ".invalid");
        var regContactUser = Guid.NewGuid().ToString("N")[..8];
        var regDeviceUuid = Guid.NewGuid().ToString("D");

        // Named handler captured in a local so it can be unsubscribed on the next register
        // (stored in _currentMessageHandler below). Listen for SIP responses on the WebSocket.
        EventHandler<SipMessageEventArgs> messageHandler = (sender, args) =>
        {
            var message = args.Message;

            // RFC 5626 §3.5.1 keep-alive pong is a bare CRLF — recognize and early-return
            // (it matches none of the SIP dispatch prefixes below anyway). Log at debug.
            if (message == "\r\n" || string.IsNullOrWhiteSpace(message))
            {
#pragma warning disable CA1848, CA1873
                _logger.LogDebug("Received keep-alive pong (bare CRLF)");
#pragma warning restore CA1848, CA1873
                return;
            }

#pragma warning disable CA1848, CA1873
            _logger.LogInformation("SIP received ({Length} chars):\n{Message}",
                message.Length, message[..Math.Min(800, message.Length)]);
#pragma warning restore CA1848, CA1873

            if (message.StartsWith("SIP/2.0", StringComparison.Ordinal))
            {
                try
                {
                    var resp = SIPResponse.ParseSIPResponse(message);
                    if (resp.Header.CSeqMethod == SIPMethodsEnum.REGISTER)
                    {
                        // Was this REGISTER already authenticated (carries our Digest)?
                        // Distinguishes the FIRST 401 (normal challenge) from a post-Digest 401.
                        var cseqNum = resp.Header.CSeq;

                        if (resp.Status == SIPResponseStatusCodesEnum.Ok)
                        {
                            LogRegistered(_logger, null);
                            _registered = true;
                            LastConnectedAt = _timeProvider.GetUtcNow().UtcDateTime;

                            // RFC 6223 keep= negotiated send-frequency (seconds) from the first
                            // response Via. RFC 5626 §3.5.1 CRLF is the keep-alive METHOD.
                            _keepAliveIntervalSeconds = ParseKeepInterval(message);

                            // Extract Service-Route for INVITE routing
                            var srIdx = message.IndexOf("Service-Route:", StringComparison.OrdinalIgnoreCase);
                            if (srIdx >= 0)
                            {
                                var srEnd = message.IndexOf("\r\n", srIdx, StringComparison.Ordinal);
                                _serviceRoute = message[(srIdx + 15)..srEnd].Trim();
                            }

                            // (Re)start the keep-alive timer now that we are registered.
                            StartKeepAlive();

                            regTcs.TrySetResult(true);
                        }
                        else if ((int)resp.Status is 401 or 407)
                        {
                            // A 401 on the FIRST REGISTER (cseq 1) is the NORMAL Digest challenge —
                            // answer it. A 401 on the SECOND REGISTER (cseq >= 2, our Digest already
                            // sent) is a REAL post-Digest auth failure → escalate.
                            if (cseqNum >= 2)
                            {
#pragma warning disable CA1848, CA1873
                                _logger.LogWarning(
                                    "Post-Digest REGISTER rejected ({Status}) — real auth failure, escalating",
                                    (int)resp.Status);
#pragma warning restore CA1848, CA1873
                                RaiseAuthenticationFailed($"REGISTER {(int)resp.Status} after Digest");
                                regTcs.TrySetResult(false);
                            }
                            else
                            {
                                // Normal 401 challenge — extract nonce and resend with Digest auth
#pragma warning disable CA1848, CA1873
                                _logger.LogInformation("Got 401 challenge, computing Digest auth...");
#pragma warning restore CA1848, CA1873
                                // Parse nonce from raw WWW-Authenticate header
                                var wwwAuth = message;
                                var nonceIdx = wwwAuth.IndexOf("nonce=\"", StringComparison.Ordinal);
                                if (nonceIdx >= 0)
                                {
                                    nonceIdx += 7;
                                    var nonceEnd = wwwAuth.IndexOf('"', nonceIdx);
                                    var nonce = wwwAuth[nonceIdx..nonceEnd];
                                    var realm = SipDomain;

                                    // Compute MD5 Digest response
                                    // HA1 = MD5(username:realm:password)
                                    // HA2 = MD5(REGISTER:sip:domain)
                                    // response = MD5(HA1:nonce:HA2)
                                    var username = creds.SipUsername;
                                    var password = creds.BearerToken; // Token used as password for Digest
                                    var uri = $"sip:{SipDomain}";

                                    var ha1 = Md5Hash($"{username}:{realm}:{password}");
                                    var ha2 = Md5Hash($"REGISTER:{uri}");
                                    var digestResponse = Md5Hash($"{ha1}:{nonce}:{ha2}");

                                    var authLine = $"Authorization: Digest algorithm=MD5, username=\"{username}\", " +
                                        $"realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{digestResponse}\"";

                                    var reg2 = BuildRegister(username, regCallId, regTag, regWsHost, regContactUser,
                                        regDeviceUuid, 2, authLine);

#pragma warning disable CA1848, CA1873
                                    _logger.LogInformation("Sending SIP REGISTER with Digest auth...");
#pragma warning restore CA1848, CA1873

                                    _ = SendSipMessageAsync(reg2);
                                }
                            }
                        }
                        else if ((int)resp.Status == 403)
                        {
                            // 403 Forbidden on REGISTER is always a real auth rejection.
#pragma warning disable CA1848, CA1873
                            _logger.LogWarning("REGISTER rejected 403 Forbidden — real auth failure, escalating");
#pragma warning restore CA1848, CA1873
                            RaiseAuthenticationFailed("REGISTER 403");
                            regTcs.TrySetResult(false);
                        }
                        else
                        {
                            LogError(_logger, $"REGISTER failed: {(int)resp.Status} {resp.ReasonPhrase}", null);
                            regTcs.TrySetResult(false);
                        }
                    }

                    // Handle INVITE responses — send PRACK for 100rel, ACK for 200
                    if (resp.Header.CSeqMethod == SIPMethodsEnum.INVITE)
                    {
                        var statusCode = (int)resp.Status;

                        if (statusCode == 183 || statusCode == 180)
                        {
                            // Extract SDP body from 183 and set as remote description
                            if (statusCode == 183)
                            {
                                var sdpSep = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                if (sdpSep >= 0)
                                {
                                    var sdpBody = message[(sdpSep + 4)..].Trim();
                                    if (sdpBody.StartsWith("v=", StringComparison.Ordinal))
                                    {
                                        var inviteCallId = ExtractHeaderValue(message, "Call-ID");
                                        if (inviteCallId is not null &&
                                            _activeCalls.TryGetValue(inviteCallId, out var callSession) &&
                                            callSession.PeerConnection is not null)
                                        {
                                            // Log remote SDP for DTLS diagnostics
#pragma warning disable CA1848, CA1873
                                        _logger.LogInformation(
                                            "Remote SDP from 183:\n{Sdp}", sdpBody);
#pragma warning restore CA1848, CA1873

                                        var answer = new SIPSorcery.Net.RTCSessionDescriptionInit
                                            {
                                                type = SIPSorcery.Net.RTCSdpType.answer,
                                                sdp = sdpBody,
                                            };
                                            var setResult = callSession.PeerConnection.setRemoteDescription(answer);
#pragma warning disable CA1848, CA1873
                                            _logger.LogInformation(
                                                "Set remote SDP from 183: {Result}, ICE={Ice}, conn={Conn}",
                                                setResult,
                                                callSession.PeerConnection.iceConnectionState,
                                                callSession.PeerConnection.connectionState);
#pragma warning restore CA1848, CA1873
                                        }
                                    }
                                }
                            }

                            // Parse raw headers from the message text — SIPSorcery may
                            // not handle Google's complex URIs correctly
                            var contactUri = ExtractHeader(message, "Contact");
                            var rseqValue = ExtractHeaderValue(message, "RSeq");
                            var toHeader = ExtractHeader(message, "To");
                            var fromHeader = ExtractHeader(message, "From");
                            var callIdValue = ExtractHeaderValue(message, "Call-ID");

                            // Extract Record-Route headers (reversed for Route in requests)
                            var recordRoutes = ExtractAllHeaders(message, "Record-Route");

                            if (rseqValue is not null && contactUri is not null
                                && callIdValue is not null
                                && _activeCalls.TryGetValue(callIdValue, out var prackSession)
                                && prackedRSeqs.Add(rseqValue)) // Only PRACK each RSeq once
                            {
                                var contactSipUri = ExtractSipUri(contactUri);
                                var currentPrackCSeq = Interlocked.Increment(ref prackSession.PrackCSeq);

                                // Build PRACK matching browser's exact format:
                                // 1. Route order: REVERSED from Record-Route (non-econt first)
                                // 2. Header order: Route, Via, Max-Forwards, To, From, ...
                                // 3. RAck: {RSeq} {INVITE_CSeq} INVITE
                                // 4. CSeq: incrementing from INVITE CSeq + 1
                                // 5. Max-Forwards: 69
                                // 6. Include all expected headers

                                var prack = $"PRACK {contactSipUri} SIP/2.0\r\n";

                                // Route headers: REVERSE order from Record-Route
                                for (int i = recordRoutes.Count - 1; i >= 0; i--)
                                {
                                    prack += $"Route: {recordRoutes[i]}\r\n";
                                }

                                prack +=
                                    $"Via: SIP/2.0/wss {_regWsHost};branch={CallProperties.CreateBranchId()};keep\r\n" +
                                    $"Max-Forwards: 69\r\n" +
                                    $"To: {toHeader}\r\n" +
                                    $"From: {fromHeader}\r\n" +
                                    $"Call-ID: {callIdValue}\r\n" +
                                    $"CSeq: {currentPrackCSeq} PRACK\r\n" +
                                    $"{XGoogleClientInfo}\r\n" +
                                    $"RAck: {rseqValue} {prackSession.InviteCSeq} INVITE\r\n" +
                                    $"Allow: INVITE,ACK,CANCEL,BYE,UPDATE,MESSAGE,OPTIONS,REFER,INFO,PRACK\r\n" +
                                    $"Supported: outbound,record-aware\r\n" +
                                    $"User-Agent: {UserAgent}\r\n" +
                                    $"Content-Length: 0\r\n" +
                                    $"\r\n";

#pragma warning disable CA1848, CA1873
                                _logger.LogInformation("Sending PRACK for {Status} (RSeq={RSeq}) to {Uri}\nPRACK body:\n{Prack}",
                                    statusCode, rseqValue, contactSipUri, prack[..Math.Min(500, prack.Length)]);
#pragma warning restore CA1848, CA1873

                                _ = SendSipMessageAsync(prack);
                            }
                        }
                        else if (statusCode == 200)
                        {
                            // 200 OK for INVITE — send ACK and store dialog state
                            var contactUri = ExtractHeader(message, "Contact");
                            var toHeader = ExtractHeader(message, "To");
                            var fromHeader = ExtractHeader(message, "From");
                            var callIdValue = ExtractHeaderValue(message, "Call-ID");
                            var recordRoutes = ExtractAllHeaders(message, "Record-Route");

                            // Extract CSeq number from response — needed for ACK
                            // (re-INVITE 200 OK has different CSeq than original INVITE)
                            var respCSeqFull = ExtractHeaderValue(message, "CSeq");
                            var respCSeqNum = (callIdValue is not null && _activeCalls.TryGetValue(callIdValue, out var ackSession))
                                ? ackSession.InviteCSeq : 1; // fallback
                            if (respCSeqFull is not null)
                            {
                                var spaceIdx = respCSeqFull.IndexOf(' ', StringComparison.Ordinal);
                                if (spaceIdx > 0 && int.TryParse(respCSeqFull[..spaceIdx],
                                    System.Globalization.NumberStyles.Integer,
                                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                                {
                                    respCSeqNum = parsed;
                                }
                            }

                            var contactSipUri = ExtractSipUri(contactUri ?? $"sip:unknown@{SipDomain}");

                            // Store dialog state for BYE
                            if (callIdValue is not null &&
                                _activeCalls.TryGetValue(callIdValue, out var invSession))
                            {
                                invSession.RemoteContactUri = contactSipUri;
                                invSession.ToHeader = toHeader;
                                invSession.FromHeader = fromHeader;
                                // Reverse Record-Route for Route set
                                invSession.RouteSet = [.. recordRoutes];
                                invSession.RouteSet.Reverse();
                                var oldInvStatus = invSession.Status;
                                invSession.Status = CallStatusType.Active;
                                CallStatusChanged?.Invoke(this, new CallStatusChangedEventArgs(callIdValue, oldInvStatus, CallStatusType.Active));
                            }

                            var ack = $"ACK {contactSipUri} SIP/2.0\r\n";

                            // Route: reversed Record-Route
                            for (int i = recordRoutes.Count - 1; i >= 0; i--)
                            {
                                ack += $"Route: {recordRoutes[i]}\r\n";
                            }

                            ack +=
                                $"Via: SIP/2.0/wss {_regWsHost};branch={CallProperties.CreateBranchId()};keep\r\n" +
                                $"Max-Forwards: 69\r\n" +
                                $"To: {toHeader}\r\n" +
                                $"From: {fromHeader}\r\n" +
                                $"Call-ID: {callIdValue}\r\n" +
                                $"CSeq: {respCSeqNum} ACK\r\n" +
                                $"Content-Length: 0\r\n" +
                                $"\r\n";

#pragma warning disable CA1848, CA1873
                            _logger.LogInformation("INVITE 200 OK — sending ACK, call CONNECTED!");
#pragma warning restore CA1848, CA1873

                            _ = SendSipMessageAsync(ack); // ACK is critical but we're in an event handler

                            // Session timer: parse Session-Expires and schedule re-INVITE
                            if (callIdValue is not null &&
                                _activeCalls.TryGetValue(callIdValue, out var timerSession))
                            {
                                var sessionExpires = ExtractHeaderValue(message, "Session-Expires");
                                if (sessionExpires is not null)
                                {
                                    // Parse "90;refresher=uac" -> 90 seconds
                                    var seParts = sessionExpires.Split(';');
                                    if (int.TryParse(seParts[0].Trim(), System.Globalization.NumberStyles.Integer,
                                        System.Globalization.CultureInfo.InvariantCulture, out var seSeconds) && seSeconds > 0)
                                    {
                                        // Refresh at half the interval (standard practice)
                                        var refreshMs = (seSeconds / 2) * 1000;
                                        var capturedCallId = callIdValue;
#pragma warning disable CA1848, CA1873
                                        _logger.LogInformation(
                                            "Session timer: {Seconds}s, refreshing every {RefreshSec}s",
                                            seSeconds, seSeconds / 2);
#pragma warning restore CA1848, CA1873

                                        timerSession.SessionTimer = new Timer(
                                            _ => SendSessionRefresh(capturedCallId),
                                            null, refreshMs, refreshMs);
                                    }
                                }
                            }
                        }
                    }
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
#pragma warning disable CA1848, CA1873
                    _logger.LogWarning(ex, "Failed to parse SIP response");
#pragma warning restore CA1848, CA1873
                }
#pragma warning restore CA1031
            }
            else if (message.StartsWith("BYE ", StringComparison.Ordinal))
            {
                // Incoming BYE — remote side hung up
                var byeCallId = ExtractHeaderValue(message, "Call-ID");
                var byeToHeader = ExtractHeader(message, "To");
                var byeFromHeader = ExtractHeader(message, "From");
                var byeVias = ExtractAllHeaders(message, "Via"); // ALL Via headers for proper routing
                var byeCSeq = ExtractHeaderValue(message, "CSeq");

#pragma warning disable CA1848, CA1873
                _logger.LogInformation("Received BYE for call {CallId} ({ViaCount} Via headers) — sending 200 OK",
                    byeCallId, byeVias.Count);
#pragma warning restore CA1848, CA1873

                // Send 200 OK response to BYE — must echo ALL Via headers for correct routing
                if (byeCallId is not null && byeVias.Count > 0)
                {
                    var byeOk = "SIP/2.0 200 OK\r\n";
                    foreach (var via in byeVias)
                    {
                        byeOk += $"Via: {via}\r\n";
                    }
                    byeOk +=
                        $"To: {byeToHeader}\r\n" +
                        $"From: {byeFromHeader}\r\n" +
                        $"Call-ID: {byeCallId}\r\n" +
                        $"CSeq: {byeCSeq}\r\n" +
                        $"Content-Length: 0\r\n" +
                        $"\r\n";

                    _ = SendSipMessageAsync(byeOk); // 200 OK to BYE is critical but we're in an event handler

                    // Clean up the call session (only on first BYE, ignore retransmissions)
#pragma warning disable CA2000
                    if (_activeCalls.TryRemove(byeCallId, out var byeSession))
#pragma warning restore CA2000
                    {
                        var oldByeStatus = byeSession.Status;
                        byeSession.Status = CallStatusType.Completed;
                        CallStatusChanged?.Invoke(this, new CallStatusChangedEventArgs(byeCallId, oldByeStatus, CallStatusType.Completed));
                        byeSession.Dispose();
                        LogCallEnded(_logger, byeCallId, null);
                    }
                }
            }
            else if (message.StartsWith("INVITE ", StringComparison.Ordinal))
            {
                HandleIncomingInvite(message);
            }
        };

        // Subscribe and remember the handlers so they can be unsubscribed on the next
        // register / teardown (the old channel is also disposed, but explicit -= avoids
        // any chance of a stale channel firing into the transport after teardown).
        channel.MessageReceived += messageHandler;
        _currentMessageHandler = messageHandler;

        EventHandler<WebSocketClosedEventArgs> closedHandler = OnChannelClosed;
        channel.Closed += closedHandler;
        _currentClosedHandler = closedHandler;

        await channel.ConnectAsync(ct).ConfigureAwait(false);

        // Store credentials for INVITE
        _credentials = creds;

        // Store for INVITE Contact header
        _regContactUser = regContactUser;
        _regWsHost = regWsHost;

        // Step 1: Send REGISTER without auth (will get 401 challenge)
        var reg1 = BuildRegister(creds.SipUsername, regCallId, regTag, regWsHost, regContactUser,
            regDeviceUuid, 1, authHeader: null);

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Sending SIP REGISTER (no auth):\n{Register}", reg1);
#pragma warning restore CA1848, CA1873

        await channel.SendAsync(reg1, ct).ConfigureAwait(false);

        // Wait for 401 or 200
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.RegisterTimeout);

        var success = await regTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        if (!success)
        {
            throw new InvalidOperationException("SIP REGISTER failed");
        }
    }

    /// <summary>
    /// Unsubscribe the current channel's handlers and dispose it. Safe to call when no
    /// channel exists. Stops the keep-alive timer too (it is restarted on the next REGISTER).
    /// </summary>
    private async Task TeardownChannelAsync()
    {
        StopKeepAlive();

        var channel = _wsChannel;
        if (channel is null)
            return;

        if (_currentMessageHandler is not null)
            channel.MessageReceived -= _currentMessageHandler;
        if (_currentClosedHandler is not null)
            channel.Closed -= _currentClosedHandler;
        _currentMessageHandler = null;
        _currentClosedHandler = null;

        try
        {
            await channel.CloseAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogDebug(ex, "Error closing previous WebSocket channel during teardown");
#pragma warning restore CA1848, CA1873
        }
#pragma warning restore CA1031

        channel.Dispose();
        _wsChannel = null;
    }

    /// <summary>
    /// Handles an unexpected channel close: marks unregistered, stops keep-alive, and kicks
    /// the single-flight reconnect loop. Intentional closes (our own CloseAsync) are ignored.
    /// </summary>
    private void OnChannelClosed(object? sender, WebSocketClosedEventArgs e)
    {
        if (e.WasIntentional)
            return; // we closed it on purpose (reconnect/teardown/dispose)

        if (_disposed || _lifetimeCts.IsCancellationRequested)
            return;

        _registered = false;
        StopKeepAlive();

        if (_activeCalls.IsEmpty)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning("SIP WebSocket dropped unexpectedly ({Desc}) — reconnecting", e.Description ?? "no detail");
#pragma warning restore CA1848, CA1873
        }
        else
        {
            // OQ2: reconnect SIGNALING only; leave the active call's RTCPeerConnection /
            // DTLS-SRTP media alone (media is peer-to-peer and can outlive a signaling gap).
            // TODO(follow-up): optionally tear down the active call so the user isn't left
            // on a dead line if the media also dies. See plan §2 Part B.5 / OQ2.
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(
                "SIP WebSocket dropped MID-CALL ({Desc}) — reconnecting signaling; " +
                "leaving {ActiveCalls} active call(s)' media untouched",
                e.Description ?? "no detail", _activeCalls.Count);
#pragma warning restore CA1848, CA1873
        }

        // Fire-and-forget; single-flight guarded inside ReconnectLoopAsync.
        _ = ReconnectLoopAsync(_lifetimeCts.Token);
    }

    /// <summary>
    /// Single-flight reconnect with capped exponential backoff + jitter. Retries indefinitely
    /// (Google may be briefly unreachable; the phone must still ring) until a REGISTER succeeds
    /// or the transport is disposed/cancelled. All reconnect triggers (Closed event, keep-alive
    /// send failure, an outbound call's EnsureRegisteredAsync) funnel through here so there is
    /// exactly one in-flight reconnect — no parallel reconnect path.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        // Single-flight guard: only one reconnect runs at a time.
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
            return;

        try
        {
            var schedule = _options.BackoffScheduleSeconds;
            var attempt = 0;
            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    await RegisterAsync(ct).ConfigureAwait(false);
                    if (_registered)
                    {
#pragma warning disable CA1848, CA1873
                        _logger.LogInformation("SIP reconnect succeeded after {Attempts} attempt(s)", attempt + 1);
#pragma warning restore CA1848, CA1873
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
#pragma warning disable CA1848, CA1873
                    _logger.LogWarning(ex, "SIP reconnect attempt {Attempt} failed", attempt + 1);
#pragma warning restore CA1848, CA1873
                }
#pragma warning restore CA1031

                // Capped exponential backoff with ±jitter.
                var idx = Math.Min(attempt, schedule.Count - 1);
                var baseSeconds = schedule[idx];
                var jitter = 1.0 + ((Random.Shared.NextDouble() * 2 - 1) * _options.BackoffJitterFraction);
                var delay = TimeSpan.FromSeconds(Math.Max(0.0, baseSeconds * jitter));
                attempt++;

                try
                {
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    private void RaiseAuthenticationFailed(string reason) =>
        AuthenticationFailed?.Invoke(this, new AuthenticationFailedEventArgs(reason));

    /// <summary>
    /// Parse the RFC 6223 <c>keep=&lt;n&gt;</c> recommended keep-alive frequency (seconds) from
    /// the first Via header of a REGISTER 200-OK. Returns the configured default
    /// (<see cref="ReconnectOptions.DefaultKeepAliveSeconds"/>) when the parameter is absent,
    /// flag-only, or unparseable.
    /// </summary>
    internal int ParseKeepInterval(string message) => ParseKeepInterval(message, _options.DefaultKeepAliveSeconds);

    /// <summary>Pure helper for parsing the RFC 6223 keep= Via parameter; testable with an explicit default.</summary>
    internal static int ParseKeepInterval(string message, int defaultSeconds)
    {
        if (string.IsNullOrEmpty(message))
            return defaultSeconds;

        // Find the first Via header line.
        var viaIdx = message.IndexOf("Via:", StringComparison.OrdinalIgnoreCase);
        if (viaIdx < 0)
            return defaultSeconds;

        var lineEnd = message.IndexOf("\r\n", viaIdx, StringComparison.Ordinal);
        var viaLine = lineEnd < 0 ? message[viaIdx..] : message[viaIdx..lineEnd];

        // Look for ";keep=<n>" (RFC 6223). A bare ";keep" flag (no value) is not a frequency.
        var keepIdx = viaLine.IndexOf("keep=", StringComparison.OrdinalIgnoreCase);
        if (keepIdx < 0)
            return defaultSeconds;

        var valueStart = keepIdx + "keep=".Length;
        var end = valueStart;
        while (end < viaLine.Length && char.IsDigit(viaLine[end]))
            end++;

        if (end == valueStart)
            return defaultSeconds; // "keep=" with non-numeric value

        if (int.TryParse(viaLine.AsSpan(valueStart, end - valueStart),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var keep) && keep > 0)
        {
            return keep;
        }

        return defaultSeconds;
    }

    /// <summary>
    /// (Re)start the keep-alive timer. Sends the RFC 5626 §3.5.1 double-CRLF ping every
    /// <c>max(floor, keep/2)</c> seconds. A send failure is the earliest drop signal → reconnect.
    /// </summary>
    private void StartKeepAlive()
    {
        StopKeepAlive();

        if (_disposed || _lifetimeCts.IsCancellationRequested)
            return;

        var keep = _keepAliveIntervalSeconds > 0 ? _keepAliveIntervalSeconds : _options.DefaultKeepAliveSeconds;
        var periodSeconds = Math.Max(_options.KeepAliveFloorSeconds, keep / 2);
        var period = TimeSpan.FromSeconds(periodSeconds);

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Keep-alive armed: keep={Keep}s, pinging every {Period}s", keep, periodSeconds);
#pragma warning restore CA1848, CA1873

        _keepAliveTimer = _timeProvider.CreateTimer(_ => SendKeepAlivePing(), null, period, period);
    }

    private void StopKeepAlive()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
    }

    private void SendKeepAlivePing()
    {
        var channel = _wsChannel;
        if (channel is null || !channel.IsConnected || _disposed || _lifetimeCts.IsCancellationRequested)
            return;

        _ = SendKeepAlivePingAsync(channel);
    }

    private async Task SendKeepAlivePingAsync(ISipWebSocketChannel channel)
    {
        try
        {
            await channel.SendPingAsync(_lifetimeCts.Token).ConfigureAwait(false);
#pragma warning disable CA1848, CA1873
            _logger.LogDebug("Sent keep-alive ping (double-CRLF)");
#pragma warning restore CA1848, CA1873
        }
        catch (OperationCanceledException) { /* shutting down */ }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A failed ping means the link is dead — trigger reconnect (single-flight guarded).
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Keep-alive ping failed — treating link as dropped, reconnecting");
#pragma warning restore CA1848, CA1873
            _registered = false;
            StopKeepAlive();
            if (!_disposed && !_lifetimeCts.IsCancellationRequested)
                _ = ReconnectLoopAsync(_lifetimeCts.Token);
        }
    }

    private static string BuildRegister(string sipUsername, string callId, string tag,
        string wsHost, string contactUser, string deviceUuid, int cseq, string? authHeader)
    {
        // Use raw string concatenation — proven working in standalone test
        var sipUsernameEncoded = Uri.EscapeDataString(sipUsername);
        var branch = CallProperties.CreateBranchId();

        var msg = $"REGISTER sip:{SipDomain} SIP/2.0\r\n" +
            $"Via: SIP/2.0/wss {wsHost};branch={branch};keep\r\n" +
            $"Max-Forwards: 69\r\n" +
            $"To: <sip:{sipUsernameEncoded}@{SipDomain}>\r\n" +
            $"From: <sip:{sipUsernameEncoded}@{SipDomain}>;tag={tag}\r\n" +
            $"Call-ID: {callId}\r\n" +
            $"CSeq: {cseq} REGISTER\r\n" +
            (authHeader is not null ? authHeader + "\r\n" : "") +
            $"X-Google-Client-Info: Ci1Hb29nbGVWb2ljZSB2b2ljZS53ZWItZnJvbnRlbmRfMjAyNjAzMTguMDhfcDESLUdvb2dsZVZvaWNlIHZvaWNlLndlYi1mcm9udGVuZF8yMDI2MDMxOC4wOF9wMRgFKhBDaHJvbWUgMTQ2LjAuMC4w\r\n" +
            $"Contact: <sip:{contactUser}@{wsHost};transport=wss>;+sip.ice;reg-id=1;+sip.instance=\"<urn:uuid:{deviceUuid}>\";expires=3600\r\n" +
            $"Expires: 3600\r\n" +
            $"Allow: INVITE,ACK,CANCEL,BYE,UPDATE,MESSAGE,OPTIONS,REFER,INFO,PRACK\r\n" +
            $"Supported: path,gruu,outbound,record-aware\r\n" +
            $"User-Agent: {UserAgent}\r\n" +
            $"Content-Length: 0\r\n" +
            $"\r\n";

        return msg;
    }

    /// <summary>Extract the SIP URI from a header value like "&lt;sip:...&gt;;params".</summary>
#pragma warning disable CA1307 // SIP URIs are ASCII — ordinal is implicit
    private static string ExtractSipUri(string headerValue)
    {
        var ltIdx = headerValue.IndexOf('<');
        if (ltIdx >= 0)
        {
            var gtIdx = headerValue.IndexOf('>', ltIdx + 1);
            if (gtIdx > ltIdx)
                return headerValue[(ltIdx + 1)..gtIdx];
        }
        return headerValue;
    }
#pragma warning restore CA1307

    /// <summary>Extract the value portion of a SIP header from raw message text.</summary>
    private static string? ExtractHeaderValue(string message, string headerName)
    {
        var idx = message.IndexOf($"{headerName}:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var valueStart = idx + headerName.Length + 1;
        var lineEnd = message.IndexOf("\r\n", valueStart, StringComparison.Ordinal);
        if (lineEnd < 0) return null;
        return message[valueStart..lineEnd].Trim();
    }

    /// <summary>Extract the full header value (e.g., Contact with URI + params).</summary>
    private static string? ExtractHeader(string message, string headerName)
    {
        return ExtractHeaderValue(message, headerName);
    }

    /// <summary>Extract all instances of a header (e.g., multiple Record-Route).</summary>
    private static List<string> ExtractAllHeaders(string message, string headerName)
    {
        var results = new List<string>();
        var search = $"{headerName}:";
        var startPos = 0;
        while (true)
        {
            var idx = message.IndexOf(search, startPos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            var valueStart = idx + search.Length;
            var lineEnd = message.IndexOf("\r\n", valueStart, StringComparison.Ordinal);
            if (lineEnd < 0) break;
            results.Add(message[valueStart..lineEnd].Trim());
            startPos = lineEnd + 2;
        }
        return results;
    }

    private static string Md5Hash(string input)
    {
#pragma warning disable CA5351 // MD5 required by SIP Digest authentication (RFC 2617)
        var hash = System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes(input));
#pragma warning restore CA5351
        return Convert.ToHexStringLower(hash);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Cancel lifetime so the keep-alive timer + any in-flight reconnect loop stop.
        try { await _lifetimeCts.CancelAsync().ConfigureAwait(false); }
#pragma warning disable CA1031
        catch (Exception ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogDebug(ex, "Error cancelling lifetime CTS during dispose");
#pragma warning restore CA1848, CA1873
        }
#pragma warning restore CA1031

        StopKeepAlive();

        foreach (var session in _activeCalls.Values)
        {
            session.Dispose();
        }
        _activeCalls.Clear();

        // Unsubscribe handlers + close/dispose the channel.
        var channel = _wsChannel;
        if (channel is not null)
        {
            if (_currentMessageHandler is not null)
                channel.MessageReceived -= _currentMessageHandler;
            if (_currentClosedHandler is not null)
                channel.Closed -= _currentClosedHandler;
            _currentMessageHandler = null;
            _currentClosedHandler = null;

            await channel.CloseAsync().ConfigureAwait(false);
            channel.Dispose();
            _wsChannel = null;
        }

        _lifetimeCts.Dispose();
    }
}

/// <summary>Per-call state — holds the RTCPeerConnection for media.</summary>
#pragma warning disable CA1812
internal sealed class SipCallSession : IDisposable
{
    public string CallId { get; }
    public SIPSorcery.Net.RTCPeerConnection? PeerConnection { get; set; }
    public Concentus.IOpusEncoder? OpusEncoder { get; set; }
    public Concentus.IOpusDecoder? OpusDecoder { get; set; }
    public CallStatusType Status { get; set; } = CallStatusType.Unknown;

    // Per-call CSeq tracking (fields, not properties, to support Interlocked.Increment)
    public int InviteCSeq;
    public int PrackCSeq;

    // Dialog state for BYE
    public string? RemoteContactUri { get; set; }
    public string? ToHeader { get; set; }
    public string? FromHeader { get; set; }
    public List<string> RouteSet { get; set; } = [];

    // Session timer
    public Timer? SessionTimer { get; set; }

    public SipCallSession(string callId)
    {
        CallId = callId;
    }

    public void Dispose()
    {
        SessionTimer?.Dispose();
        (OpusDecoder as IDisposable)?.Dispose();
        (OpusEncoder as IDisposable)?.Dispose();
        PeerConnection?.close();
    }
}
#pragma warning restore CA1812
