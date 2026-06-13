using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Drives <see cref="GvSipTransport"/> against a <see cref="FakeSipWebSocketChannel"/> to verify
/// the inbound-CANCEL path: when the cell caller hangs up BEFORE the rotary phone is answered,
/// GV sends a SIP CANCEL on the WSS signaling channel. The transport must 200-OK that CANCEL and
/// fire <c>CallStatusChanged(Completed)</c> for the call so the downstream teardown chain
/// (GVApiAdapter.OnCallEnded → CallManager.HangUp → CancelPendingInvite) stops the HT801 ringing.
/// </summary>
public class GvSipTransportCancelTests
{
    private static readonly SipCredentials TestCreds =
        new(SipUsername: "sip-token", BearerToken: "crypto-key", PhoneNumber: "+15551234567", ExpirySeconds: 3600);

    private const string TestCallId = "inbound-cancel-call-id-001";

    /// <summary>Build a REGISTER 200-OK that SIPSorcery parses and carries keep=N in the Via.</summary>
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
    /// A minimal inbound INVITE with a single Via and a tiny SDP offer — enough for
    /// <c>HandleIncomingInvite</c> to register the call in <c>_activeCalls</c>, answer it, and
    /// raise <see cref="GvSipTransport.IncomingCallReceived"/>.
    /// </summary>
    private static string InboundInvite(string callId) =>
        $"INVITE sip:{Guid.NewGuid():N}@web.c.pbx.voice.sip.google.com SIP/2.0\r\n" +
        "Via: SIP/2.0/WSS proxy.voice.google.com;branch=z9hG4bK-invite-1\r\n" +
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

    /// <summary>
    /// An inbound CANCEL for the given Call-ID with TWO Via headers (to prove all are echoed),
    /// the same Call-ID/CSeq as the INVITE, and a To/From.
    /// </summary>
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

    [Fact]
    public async Task InboundCancel_AfterInvite_FiresCompleted_AndSends200Ok()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = CreateTransport(fake);
        await transport.EnsureRegisteredAsync();

        IncomingCallInfo? incoming = null;
        transport.IncomingCallReceived += (_, e) => incoming = e.CallInfo;

        CallStatusChangedEventArgs? completed = null;
        transport.CallStatusChanged += (_, e) =>
        {
            if (e.NewStatus == CallStatusType.Completed && e.CallId == TestCallId)
                completed = e;
        };

        // 1) Inbound INVITE — registers the call and fires IncomingCallReceived.
        fake.FeedMessage(InboundInvite(TestCallId));
        var gotInvite = await WaitForAsync(() => incoming is not null);
        Assert.True(gotInvite, "expected IncomingCallReceived for the inbound INVITE");
        Assert.Equal(TestCallId, incoming!.CallId);

        // 2) Inbound CANCEL with the same Call-ID — caller hung up before answer.
        fake.FeedMessage(InboundCancel(TestCallId));

        // 3) Completed must fire for that Call-ID (this is what drives HT801 teardown).
        var gotCompleted = await WaitForAsync(() => completed is not null);
        Assert.True(gotCompleted, "expected CallStatusChanged(Completed) after inbound CANCEL");
        Assert.Equal(TestCallId, completed!.CallId);
        Assert.Equal(CallStatusType.Completed, completed.NewStatus);

        // 4) A 200 OK to the CANCEL must have been sent on the channel.
        var ok200 = fake.Sends.FirstOrDefault(s =>
            s.StartsWith("SIP/2.0 200 OK", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal) &&
            s.Contains("CSeq: 1 CANCEL", StringComparison.Ordinal));
        Assert.NotNull(ok200);

        // 5) RFC 3261 §15: the original INVITE transaction must be terminated with a 487.
        // Its CSeq method is INVITE (the request being terminated), not CANCEL.
        var terminated = fake.Sends.FirstOrDefault(s =>
            s.StartsWith("SIP/2.0 487 Request Terminated", StringComparison.Ordinal) &&
            s.Contains($"Call-ID: {TestCallId}", StringComparison.Ordinal) &&
            s.Contains("CSeq: 1 INVITE", StringComparison.Ordinal));
        Assert.NotNull(terminated);
    }

    [Fact]
    public async Task InboundCancel_200Ok_EchoesAllViaHeaders_AndOriginalCallIdAndCSeq()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = CreateTransport(fake);
        await transport.EnsureRegisteredAsync();

        var incomingFired = false;
        transport.IncomingCallReceived += (_, _) => incomingFired = true;

        fake.FeedMessage(InboundInvite(TestCallId));
        await WaitForAsync(() => incomingFired);

        fake.FeedMessage(InboundCancel(TestCallId));

        var ok200 = await WaitForAsync(() => fake.Sends.Any(s =>
            s.StartsWith("SIP/2.0 200 OK", StringComparison.Ordinal) &&
            s.Contains("CSeq: 1 CANCEL", StringComparison.Ordinal)));
        Assert.True(ok200, "expected a 200 OK to the CANCEL");

        var response = fake.Sends.First(s =>
            s.StartsWith("SIP/2.0 200 OK", StringComparison.Ordinal) &&
            s.Contains("CSeq: 1 CANCEL", StringComparison.Ordinal));

        // Both Via headers from the CANCEL must be echoed (RFC 3261 — preserve the Via stack).
        Assert.Contains("Via: SIP/2.0/WSS proxy.voice.google.com;branch=z9hG4bK-invite-1", response, StringComparison.Ordinal);
        Assert.Contains("Via: SIP/2.0/WSS edge.voice.google.com;branch=z9hG4bK-edge-9", response, StringComparison.Ordinal);

        // References the original Call-ID and the CANCEL CSeq.
        Assert.Contains($"Call-ID: {TestCallId}", response, StringComparison.Ordinal);
        Assert.Contains("CSeq: 1 CANCEL", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboundCancel_ForUnknownCallId_DoesNotFireCompleted_OrThrow()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = CreateTransport(fake);
        await transport.EnsureRegisteredAsync();

        var completedCount = 0;
        transport.CallStatusChanged += (_, e) =>
        {
            if (e.NewStatus == CallStatusType.Completed)
                Interlocked.Increment(ref completedCount);
        };

        // CANCEL for a call that was never seen — no _activeCalls entry.
        // (No exception escapes the message handler; the 200 OK may still be sent, but no
        //  Completed event fires because there's no session to remove.)
        fake.FeedMessage(InboundCancel("never-seen-call-id"));

        // Give any (erroneous) Completed event time to surface.
        await Task.Delay(200);
        Assert.Equal(0, completedCount);
    }
}
