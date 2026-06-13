using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Drives <see cref="GvSipTransport"/> against a <see cref="FakeSipWebSocketChannel"/> to verify the
/// DEFERRED-ANSWER inbound path: an inbound INVITE is answered with 180 Ringing only — the 200 OK is
/// HELD until the handset is lifted (<see cref="GvSipTransport.AcceptIncomingCallAsync"/>). Holding the
/// 200 OK is what makes Google Voice send a proper SIP CANCEL when the cell caller hangs up during the
/// ring (auto-answering suppressed that CANCEL). The held 200 OK is sent only on handset-lift; a
/// pre-200 hangup declines with a 480, never a BYE.
/// </summary>
public class GvSipTransportInboundDeferredAnswerTests
{
    private static readonly SipCredentials TestCreds =
        new(SipUsername: "sip-token", BearerToken: "crypto-key", PhoneNumber: "+15551234567", ExpirySeconds: 3600);

    private const string TestCallId = "inbound-deferred-call-id-001";

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

    private static GvSipTransport CreateTransport(FakeSipWebSocketChannel fake) =>
        new(
            NullLogger<GvSipTransport>.Instance,
            () => Task.FromResult(TestCreds),
            loggerFactory: null,
            channelFactory: (_, _) => fake,
            options: null);

    /// <summary>
    /// A minimal inbound INVITE with two Via headers and a tiny SDP offer — enough for
    /// <c>HandleIncomingInvite</c> to register the call (now in Ringing), send 180, pre-build the
    /// held 200 OK, and raise <see cref="GvSipTransport.IncomingCallReceived"/>.
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

    /// <summary>An inbound CANCEL for the given Call-ID with TWO Via headers and CSeq "1 CANCEL".</summary>
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

    private static bool IsOk200WithSdp(string s) =>
        s.StartsWith("SIP/2.0 200 OK", StringComparison.Ordinal) &&
        s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal) &&
        s.Contains("Content-Type: application/sdp", StringComparison.Ordinal);

    private static async Task<GvSipTransport> RegisteredTransportWithRingingCallAsync(FakeSipWebSocketChannel fake)
    {
        var transport = CreateTransport(fake);
        await transport.EnsureRegisteredAsync();

        var incomingFired = false;
        transport.IncomingCallReceived += (_, _) => incomingFired = true;

        fake.FeedMessage(InboundInvite(TestCallId));
        await WaitForAsync(() => incomingFired);
        return transport;
    }

    // 1) Inbound INVITE → 180 Ringing sent, NO 200 OK sent (the answer is deferred).
    [Fact]
    public async Task InboundInvite_Sends180Ringing_ButHoldsThe200Ok()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = await RegisteredTransportWithRingingCallAsync(fake);

        var ringing = fake.Sends.FirstOrDefault(s =>
            s.StartsWith("SIP/2.0 180 Ringing", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal));
        Assert.NotNull(ringing);

        // No 200 OK carrying SDP for this call should have been sent yet.
        Assert.DoesNotContain(fake.Sends, IsOk200WithSdp);
    }

    // 2) AcceptIncomingCallAsync → the held 200 OK (with Content-Type: application/sdp) is now sent.
    [Fact]
    public async Task AcceptIncomingCall_SendsHeld200OkWithSdp()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = await RegisteredTransportWithRingingCallAsync(fake);

        Assert.DoesNotContain(fake.Sends, IsOk200WithSdp); // not before accept

        await transport.AcceptIncomingCallAsync(TestCallId);

        var ok200 = await WaitForAsync(() => fake.Sends.Any(IsOk200WithSdp));
        Assert.True(ok200, "expected the held 200 OK (with SDP) to be sent after AcceptIncomingCallAsync");
    }

    // 3) CANCEL during ring → 200 OK to CANCEL + 487 to INVITE, CallStatusChanged(Completed) raised.
    //    Validates the #39 CANCEL handler interplay with the now-Ringing (pre-200) session.
    [Fact]
    public async Task CancelDuringRing_Sends200ToCancel_And487ToInvite_AndFiresCompleted()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = await RegisteredTransportWithRingingCallAsync(fake);

        CallStatusChangedEventArgs? completed = null;
        transport.CallStatusChanged += (_, e) =>
        {
            if (e.NewStatus == CallStatusType.Completed && e.CallId == TestCallId)
                completed = e;
        };

        fake.FeedMessage(InboundCancel(TestCallId));

        var gotCompleted = await WaitForAsync(() => completed is not null);
        Assert.True(gotCompleted, "expected CallStatusChanged(Completed) after CANCEL during ring");

        var ok200 = fake.Sends.FirstOrDefault(s =>
            s.StartsWith("SIP/2.0 200 OK", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal) &&
            s.Contains("CSeq: 1 CANCEL", StringComparison.Ordinal));
        Assert.NotNull(ok200);

        var terminated = fake.Sends.FirstOrDefault(s =>
            s.StartsWith("SIP/2.0 487 Request Terminated", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal) &&
            s.Contains("CSeq: 1 INVITE", StringComparison.Ordinal));
        Assert.NotNull(terminated);
    }

    // 4) CANCEL after AcceptIncomingCallAsync → ignored (no 487; the call is already Active).
    [Fact]
    public async Task CancelAfterAccept_DoesNotSend487_BecauseCallIsActive()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = await RegisteredTransportWithRingingCallAsync(fake);

        // Answer the call first — session moves Ringing -> Active and is no longer cancelable.
        await transport.AcceptIncomingCallAsync(TestCallId);
        await WaitForAsync(() => fake.Sends.Any(IsOk200WithSdp));

        // A late CANCEL: the call is already answered (no pre-200 INVITE transaction to terminate).
        fake.FeedMessage(InboundCancel(TestCallId));
        await Task.Delay(200);

        var terminated = fake.Sends.FirstOrDefault(s =>
            s.StartsWith("SIP/2.0 487 Request Terminated", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal));
        Assert.Null(terminated);
    }

    // 5) HangupAsync on a Ringing session → 480 sent, NOT a BYE (no confirmed dialog to BYE).
    [Fact]
    public async Task HangupAsync_OnRingingSession_Sends480_NotBye()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = await RegisteredTransportWithRingingCallAsync(fake);

        await transport.HangupAsync(TestCallId);

        var decline = await WaitForAsync(() => fake.Sends.Any(s =>
            s.StartsWith("SIP/2.0 480 Temporarily Unavailable", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal)));
        Assert.True(decline, "expected a 480 Temporarily Unavailable for the declined ringing call");

        // The 480 must echo the INVITE's Via stack (both Via headers) and carry the mandatory
        // To/From/CSeq per RFC 3261 §8.2.6 (so GV accepts it and clears the transaction).
        var declineMsg = fake.Sends.First(s =>
            s.StartsWith("SIP/2.0 480 Temporarily Unavailable", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal));
        Assert.Contains("Via: SIP/2.0/WSS proxy.voice.google.com;branch=z9hG4bK-invite-1", declineMsg, StringComparison.Ordinal);
        Assert.Contains("Via: SIP/2.0/WSS edge.voice.google.com;branch=z9hG4bK-edge-9", declineMsg, StringComparison.Ordinal);
        Assert.Contains("To: ", declineMsg, StringComparison.Ordinal);
        Assert.Contains("From: \"Caller\" <sip:+15558675309@web.c.pbx.voice.sip.google.com>;tag=caller-tag", declineMsg, StringComparison.Ordinal);
        Assert.Contains("CSeq: 1 INVITE", declineMsg, StringComparison.Ordinal);

        // No BYE should have been sent for a pre-200 (un-confirmed) dialog.
        Assert.DoesNotContain(fake.Sends, s => s.StartsWith("BYE ", StringComparison.Ordinal));
    }

    // 5b) A CANCEL that wins the race (removes the ringing session) must make a following
    //     AcceptIncomingCallAsync a no-op — NO held 200 OK is sent for a cancelled call.
    [Fact]
    public async Task AcceptAfterCancel_DoesNotSendHeld200Ok()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = await RegisteredTransportWithRingingCallAsync(fake);

        // CANCEL arrives first and tears the ringing call down (caller hung up).
        fake.FeedMessage(InboundCancel(TestCallId));
        await WaitForAsync(() => fake.Sends.Any(s =>
            s.StartsWith("SIP/2.0 487 Request Terminated", StringComparison.Ordinal)));

        // Now the handset lifts a moment too late — accept must find no ringing session and bail.
        await transport.AcceptIncomingCallAsync(TestCallId);
        await Task.Delay(100);

        Assert.DoesNotContain(fake.Sends, IsOk200WithSdp);
    }

    // 6) AcceptIncomingCallAsync fires CallStatusChanged(Active).
    [Fact]
    public async Task AcceptIncomingCall_FiresCallStatusChanged_RingingToActive()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = await RegisteredTransportWithRingingCallAsync(fake);

        CallStatusChangedEventArgs? active = null;
        transport.CallStatusChanged += (_, e) =>
        {
            if (e.NewStatus == CallStatusType.Active && e.CallId == TestCallId)
                active = e;
        };

        await transport.AcceptIncomingCallAsync(TestCallId);

        var gotActive = await WaitForAsync(() => active is not null);
        Assert.True(gotActive, "expected CallStatusChanged(Active) on AcceptIncomingCallAsync");
        Assert.Equal(CallStatusType.Ringing, active!.OldStatus);
        Assert.Equal(CallStatusType.Active, active.NewStatus);
    }
}
