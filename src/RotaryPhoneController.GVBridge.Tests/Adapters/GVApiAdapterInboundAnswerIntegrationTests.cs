using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Sip;
using RotaryPhoneController.GVBridge.Tests.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Adapters;

/// <summary>
/// THE mandatory regression test the PR #40 gap demanded. PR #40's unit tests called
/// <c>AcceptIncomingCallAsync(literalCallId)</c> directly, bypassing the REAL inbound chain
/// (INVITE -> IncomingCallReceived -> HandleSipIncomingCall arms <c>_activeCallId</c> ->
/// OnCallAnsweredOnRotaryPhoneAsync -> AcceptIncomingCallAsync(_activeCallId)) AND never fed a
/// CANCEL/BYE during the ring. So the unit tests passed while real answering was broken (the
/// CANCEL handler had evicted the Ringing session, so the held 200 OK was never sent on handset-lift).
///
/// These tests drive the genuine production path end-to-end:
///   - a real <see cref="GvSipTransport"/> over a <see cref="FakeSipWebSocketChannel"/>,
///   - wired into a real <see cref="GVApiAdapter"/> with the EXACT production subscriptions
///     (<c>AttachSipTransportForTest</c> calls the same WireSipTransportEvents ActivateAsync uses),
///   - an inbound INVITE with a TRANSPORT-SIDE, non-constant Call-ID (NOT a literal the test passes
///     to accept) so the test only ever answers via <c>_activeCallId</c>.
/// </summary>
public class GVApiAdapterInboundAnswerIntegrationTests
{
    private static readonly SipCredentials TestCreds =
        new(SipUsername: "sip-token", BearerToken: "crypto-key", PhoneNumber: "+15551234567", ExpirySeconds: 3600);

    private static string Register200Ok(int keep = 240) =>
        "SIP/2.0 200 OK\r\n" +
        $"Via: SIP/2.0/WSS abc123.invalid;branch=z9hG4bK-test;keep={keep}\r\n" +
        "To: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=server-tag\r\n" +
        "From: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=client-tag\r\n" +
        "Call-ID: test-call-id\r\n" +
        "CSeq: 1 REGISTER\r\n" +
        "Service-Route: <sip:proxy.voice.google.com;lr>\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    private static Action<string, FakeSipWebSocketChannel> RegisterAutoResponder() =>
        (payload, ch) =>
        {
            if (payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                ch.FeedMessage(Register200Ok());
        };

    /// <summary>
    /// An inbound INVITE carrying a transport-side Call-ID — the value the transport reads from the
    /// SIP message and arms as <c>_activeCallId</c>. The test NEVER passes this literal to accept; it
    /// only ever answers via <c>adapter.OnCallAnsweredOnRotaryPhoneAsync()</c> -> <c>_activeCallId</c>.
    /// </summary>
    private static string InboundInvite(string callId) =>
        $"INVITE sip:{Guid.NewGuid():N}@web.c.pbx.voice.sip.google.com SIP/2.0\r\n" +
        "Via: SIP/2.0/WSS proxy.voice.google.com;branch=z9hG4bK-invite-1\r\n" +
        "Via: SIP/2.0/WSS edge.voice.google.com;branch=z9hG4bK-edge-9\r\n" +
        "Max-Forwards: 70\r\n" +
        "To: <sip:sip-token@web.c.pbx.voice.sip.google.com>\r\n" +
        "From: \"Caller\" <sip:+15558675309@web.c.pbx.voice.sip.google.com>;tag=caller-tag\r\n" +
        $"Call-ID: {callId}\r\n" +
        "CSeq: 1 INVITE\r\n" +
        "Contact: <sip:caller@proxy.voice.google.com;transport=wss>\r\n" +
        "Content-Type: application/sdp\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n" +
        "v=0\r\n" +
        "o=- 100 100 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n" +
        "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
        "c=IN IP4 127.0.0.1\r\n" +
        "a=rtpmap:111 OPUS/48000/2\r\n" +
        "a=sendrecv\r\n";

    private static string InboundCancel(string callId) =>
        $"CANCEL sip:{Guid.NewGuid():N}@web.c.pbx.voice.sip.google.com SIP/2.0\r\n" +
        "Via: SIP/2.0/WSS proxy.voice.google.com;branch=z9hG4bK-invite-1\r\n" +
        "Via: SIP/2.0/WSS edge.voice.google.com;branch=z9hG4bK-edge-9\r\n" +
        "Max-Forwards: 70\r\n" +
        "To: <sip:sip-token@web.c.pbx.voice.sip.google.com>\r\n" +
        "From: \"Caller\" <sip:+15558675309@web.c.pbx.voice.sip.google.com>;tag=caller-tag\r\n" +
        $"Call-ID: {callId}\r\n" +
        "CSeq: 1 CANCEL\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return condition();
    }

    private static bool IsOk200WithSdp(string s, string callId) =>
        s.StartsWith("SIP/2.0 200 OK", StringComparison.Ordinal) &&
        s.Contains($"Call-ID: {callId}", StringComparison.Ordinal) &&
        s.Contains("Content-Type: application/sdp", StringComparison.Ordinal);

    private static GVApiAdapter CreateAdapter() =>
        new(
            Options.Create(new GVBridgeConfig
            {
                GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
                GvApiKey = "test",
                CookieFilePath = "test.enc",
                CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
            }),
            NullLogger<GVApiAdapter>.Instance,
            NullLoggerFactory.Instance);

    private static GvSipTransport CreateTransport(FakeSipWebSocketChannel fake) =>
        new(
            NullLogger<GvSipTransport>.Instance,
            () => Task.FromResult(TestCreds),
            loggerFactory: null,
            channelFactory: (_, _) => fake,
            options: null);

    /// <summary>
    /// Stand up a registered transport + adapter wired exactly as production, feed an inbound INVITE
    /// with a transport-side Call-ID, and wait for the adapter's OnIncomingCall to fire (proving the
    /// real INVITE -> IncomingCallReceived -> HandleSipIncomingCall chain armed _activeCallId).
    /// </summary>
    private static async Task<(GVApiAdapter adapter, GvSipTransport transport, FakeSipWebSocketChannel fake, string callId)>
        SetUpRingingInboundCallAsync()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        var transport = CreateTransport(fake);
        await transport.EnsureRegisteredAsync();

        var adapter = CreateAdapter();
        adapter.AttachSipTransportForTest(transport); // wires HandleSipIncomingCall + HandleSipCallStatusChanged

        var callId = $"transport-side-{Guid.NewGuid():N}";

        var incomingNumber = (string?)null;
        adapter.OnIncomingCall += n => incomingNumber = n;

        fake.FeedMessage(InboundInvite(callId));
        var gotIncoming = await WaitForAsync(() => incomingNumber is not null);
        Assert.True(gotIncoming, "expected adapter.OnIncomingCall to fire from the real INVITE chain");

        return (adapter, transport, fake, callId);
    }

    // THE mandatory test: normal answer through the REAL path sends the held 200 OK with SDP.
    [Fact]
    public async Task RealPath_NormalAnswer_SendsHeld200OkWithSdp()
    {
        var (adapter, transport, fake, callId) = await SetUpRingingInboundCallAsync();
        await using var _ = transport;

        // 180 Ringing sent, but NO 200 OK yet (the answer is deferred until handset-lift).
        Assert.Contains(fake.Sends, s =>
            s.StartsWith("SIP/2.0 180 Ringing", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {callId}", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Sends, s => IsOk200WithSdp(s, callId));

        // Handset lifts: this internally calls AcceptIncomingCallAsync(_activeCallId) — the EXACT
        // chain PR #40 left untested. (audioBridge is null, so only the answer path runs.)
        await adapter.OnCallAnsweredOnRotaryPhoneAsync();

        // THE assertion PR #40 lacked: the held 200 OK (with application/sdp) IS now on the channel.
        var ok200 = await WaitForAsync(() => fake.Sends.Any(s => IsOk200WithSdp(s, callId)));
        Assert.True(ok200,
            "REGRESSION: held 200 OK (with SDP) must be sent on handset-lift via the real " +
            "OnCallAnsweredOnRotaryPhoneAsync -> AcceptIncomingCallAsync(_activeCallId) chain");
    }

    // Cancel-race: a CANCEL during the ring then a handset-lift must NOT send a 200 OK, and the
    // teardown (OnCallEnded) must fire — answer-vs-cancelled correctly distinguished, and crucially
    // the session lookup STILL WORKS after the cancel (the #40 bug evicted it).
    [Fact]
    public async Task RealPath_CancelDuringRing_ThenAnswer_SendsNo200Ok_AndFiresTeardown()
    {
        var (adapter, transport, fake, callId) = await SetUpRingingInboundCallAsync();
        await using var _ = transport;

        var endedCount = 0;
        adapter.OnCallEnded += () => Interlocked.Increment(ref endedCount);

        // Caller hangs up during the ring: GV sends a CANCEL on the signaling channel.
        fake.FeedMessage(InboundCancel(callId));

        // The CANCEL drives the teardown chain (CallStatusChanged(Completed) -> OnCallEnded) — exactly once.
        var gotEnded = await WaitForAsync(() => Volatile.Read(ref endedCount) > 0);
        Assert.True(gotEnded, "expected OnCallEnded to fire after the CANCEL during ring");
        var endedAfterCancel = Volatile.Read(ref endedCount);

        // Handset lifts a moment too late. The accept must find the (still-present, Cancelled)
        // session, read Cancelled, and be a TRUE NO-OP: NO 200 OK to a caller who is gone, AND no
        // SECOND teardown event (the CANCEL handler already fired Completed; teardown is in flight).
        await adapter.OnCallAnsweredOnRotaryPhoneAsync();
        await Task.Delay(100);

        Assert.DoesNotContain(fake.Sends, s => IsOk200WithSdp(s, callId));
        Assert.Equal(endedAfterCancel, Volatile.Read(ref endedCount)); // no double-teardown from the late accept
    }

    // Belt-and-braces: the same real-path normal-answer assertion when an inbound call is followed by
    // a SECOND inbound INVITE (different transport-side Call-ID). The latest INVITE wins _activeCallId
    // and that is the one answered — proves the answer never targets a stale/constant id.
    [Fact]
    public async Task RealPath_NormalAnswer_AnswersLatestActiveCallId()
    {
        var (adapter, transport, fake, firstCallId) = await SetUpRingingInboundCallAsync();
        await using var _ = transport;

        var secondCallId = $"transport-side-{Guid.NewGuid():N}";
        var secondIncoming = false;
        adapter.OnIncomingCall += _ => secondIncoming = true;
        fake.FeedMessage(InboundInvite(secondCallId));
        await WaitForAsync(() => secondIncoming);

        await adapter.OnCallAnsweredOnRotaryPhoneAsync();

        var answeredSecond = await WaitForAsync(() => fake.Sends.Any(s => IsOk200WithSdp(s, secondCallId)));
        Assert.True(answeredSecond, "expected the latest INVITE's call to be the one answered via _activeCallId");
        Assert.NotEqual(firstCallId, secondCallId);
    }
}
