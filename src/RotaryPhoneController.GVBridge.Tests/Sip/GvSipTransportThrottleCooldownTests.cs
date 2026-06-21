using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Tests for the 603/403 REGISTER throttle cooldown that breaks the 2026-06-19 feedback loop:
/// a Google account-level "603 Declined" throttle caused an endless re-register storm because
/// every recovery attempt immediately re-REGISTERed straight back into the throttle. The fix
/// makes the transport enter an escalating cooldown during which NO REGISTER is sent to Google,
/// so the account-level throttle can actually cool.
///
/// Uses a <see cref="FakeTimeProvider"/> so the cooldown window can be advanced deterministically
/// without real waits; the transport does all cooldown math via its injected TimeProvider.
/// </summary>
public class GvSipTransportThrottleCooldownTests
{
    private static readonly SipCredentials TestCreds =
        new(SipUsername: "sip-token", BearerToken: "crypto-key", PhoneNumber: "+15551234567", ExpirySeconds: 3600);

    private static GvSipTransport CreateTransport(
        FakeSipWebSocketChannel fake,
        ReconnectOptions options,
        TimeProvider timeProvider,
        Func<Task<SipCredentials>>? getCreds = null)
    {
        return new GvSipTransport(
            NullLogger<GvSipTransport>.Instance,
            getCreds ?? (() => Task.FromResult(TestCreds)),
            loggerFactory: null,
            channelFactory: (_, _) => fake,
            options: options,
            timeProvider: timeProvider);
    }

    /// <summary>A 200-OK REGISTER response SIPSorcery parses.</summary>
    private static string Register200Ok() =>
        "SIP/2.0 200 OK\r\n" +
        "Via: SIP/2.0/WSS abc123.invalid;branch=z9hG4bK-test;keep=240\r\n" +
        "To: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=server-tag\r\n" +
        "From: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=client-tag\r\n" +
        "Call-ID: test-call-id\r\n" +
        "CSeq: 2 REGISTER\r\n" +
        "Service-Route: <sip:proxy.voice.google.com;lr>\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    private static string Register603Declined(int cseq) =>
        "SIP/2.0 603 Declined\r\n" +
        "Via: SIP/2.0/WSS abc123.invalid;branch=z9hG4bK-test\r\n" +
        "To: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=server-tag\r\n" +
        "From: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=client-tag\r\n" +
        "Call-ID: test-call-id\r\n" +
        $"CSeq: {cseq} REGISTER\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    private static string Register401Challenge(int cseq) =>
        "SIP/2.0 401 Unauthorized\r\n" +
        "Via: SIP/2.0/WSS abc123.invalid;branch=z9hG4bK-test\r\n" +
        "To: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=server-tag\r\n" +
        "From: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=client-tag\r\n" +
        "Call-ID: test-call-id\r\n" +
        $"CSeq: {cseq} REGISTER\r\n" +
        "WWW-Authenticate: Digest realm=\"web.c.pbx.voice.sip.google.com\", nonce=\"abc123nonce\"\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    /// <summary>Count of post-Digest (cseq >= 2) REGISTER sends — the ones that actually hit Google's throttle.</summary>
    private static int PostDigestRegisterCount(FakeSipWebSocketChannel fake) =>
        fake.Sends.Count(s =>
            s.StartsWith("REGISTER ", StringComparison.Ordinal) &&
            !s.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal));

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
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

    /// <summary>Options with no rate-floor and instant backoff so timing is driven only by the FakeTimeProvider.</summary>
    private static ReconnectOptions Options(IReadOnlyList<int> cooldown) => new()
    {
        DefaultKeepAliveSeconds = 240,
        KeepAliveFloorSeconds = 0,
        BackoffScheduleSeconds = [0],
        BackoffJitterFraction = 0.0,
        RegisterTimeout = TimeSpan.FromSeconds(2),
        MinRegisterIntervalSeconds = 0,
        ThrottleCooldownScheduleSeconds = cooldown,
    };

    [Fact]
    public async Task Register603_RepeatedDeclines_EntersCooldown_SuppressesRegisterAttempts()
    {
        // A long cooldown so the window stays open for the whole test — we assert NO further
        // REGISTER is sent to Google while throttled. This is THE core regression: current code
        // sends a fresh REGISTER on every recovery attempt straight back into the throttle.
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;
            var cseq = payload.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal) ? 1 : 2;
            ch.FeedMessage(cseq == 1 ? Register401Challenge(1) : Register603Declined(2));
        };

        var time = new FakeTimeProvider();
        await using var transport = CreateTransport(fake, Options([3600]), time);

        // First attempt: 401 -> Digest -> 603. Throws (failed register) and enters cooldown.
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());

        Assert.True(transport.IsThrottled, "a post-Digest 603 must enter the throttle cooldown");
        Assert.False(transport.IsRegistered, "transport must report NOT registered while throttled");

        var sentDuringThrottle = PostDigestRegisterCount(fake);
        Assert.Equal(1, sentDuringThrottle); // exactly the one decline so far

        // Simulate the reconnect/recovery path re-attempting register WHILE throttled. This is what
        // ForceReRegister / ReconnectLoop / watchdog all do. The gate must SUPPRESS it: no REGISTER
        // is sent to Google. Each attempt should throw without sending.
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());

        // Give any erroneous async send a chance to surface.
        await Task.Delay(150);

        Assert.Equal(1, PostDigestRegisterCount(fake)); // STILL just the original — zero new REGISTERs sent
        Assert.True(transport.IsThrottled);
    }

    [Fact]
    public async Task Register603_Escalates_CooldownGrows()
    {
        // schedule [1, 5] seconds: first throttle -> 1s window, second -> 5s window (cap).
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;
            var cseq = payload.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal) ? 1 : 2;
            ch.FeedMessage(cseq == 1 ? Register401Challenge(1) : Register603Declined(2));
        };

        var time = new FakeTimeProvider();
        await using var transport = CreateTransport(fake, Options([1, 5]), time);

        // First decline -> 1s cooldown.
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());
        Assert.True(transport.IsThrottled);
        var firstUntil = transport.ThrottledUntil;
        Assert.NotNull(firstUntil);

        // Still inside the 1s window: suppressed (no send), still throttled.
        time.Advance(TimeSpan.FromMilliseconds(900));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());
        Assert.True(transport.IsThrottled);
        Assert.Equal(1, PostDigestRegisterCount(fake)); // no new REGISTER while still in window

        // Past the 1s window: the next attempt IS allowed through, sends a REGISTER, gets another
        // 603 -> escalates to the 5s window.
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());
        Assert.Equal(2, PostDigestRegisterCount(fake)); // one new REGISTER got through after window expired
        Assert.True(transport.IsThrottled);
        var secondUntil = transport.ThrottledUntil;
        Assert.NotNull(secondUntil);

        // The second cooldown window must be LONGER than the first (escalation: 1s -> 5s).
        Assert.True(secondUntil > firstUntil, $"second window {secondUntil:o} should extend past first {firstUntil:o}");
    }

    [Fact]
    public async Task Register200_AfterThrottle_ResetsCooldown()
    {
        // First post-Digest REGISTER -> 603 (cooldown). After the window, the next REGISTER -> 200-OK,
        // which must RESET the throttle so a later attempt is not suppressed.
        var declineNext = true;
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;
            if (payload.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal))
            {
                ch.FeedMessage(Register401Challenge(1));
                return;
            }
            // post-Digest: first one declines, subsequent ones succeed
            if (declineNext)
            {
                declineNext = false;
                ch.FeedMessage(Register603Declined(2));
            }
            else
            {
                ch.FeedMessage(Register200Ok());
            }
        };

        var time = new FakeTimeProvider();
        await using var transport = CreateTransport(fake, Options([1]), time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());
        Assert.True(transport.IsThrottled);
        Assert.NotNull(transport.ThrottledUntil);
        Assert.NotNull(transport.ThrottleReason);

        // Advance past the 1s cooldown, then re-register -> 200-OK.
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.EnsureRegisteredAsync();

        Assert.True(transport.IsRegistered, "should be registered after the 200-OK");
        Assert.False(transport.IsThrottled, "200-OK must clear the throttle");
        Assert.Null(transport.ThrottledUntil);
        Assert.Null(transport.ThrottleReason);

        // A subsequent ensure (already registered) is a no-op and must not be suppressed.
        await transport.EnsureRegisteredAsync();
        Assert.True(transport.IsRegistered);
    }

    [Fact]
    public async Task Throttle_SurfacesHonestStatus_WhileActive_AndNullAfterReset()
    {
        var declineNext = true;
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;
            if (payload.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal))
            {
                ch.FeedMessage(Register401Challenge(1));
                return;
            }
            if (declineNext)
            {
                declineNext = false;
                ch.FeedMessage(Register603Declined(2));
            }
            else
            {
                ch.FeedMessage(Register200Ok());
            }
        };

        var time = new FakeTimeProvider();
        await using var transport = CreateTransport(fake, Options([1]), time);

        // Before any throttle: all clear.
        Assert.False(transport.IsThrottled);
        Assert.Null(transport.ThrottledUntil);
        Assert.Null(transport.ThrottleReason);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());

        // While throttled: both surfaced honestly.
        Assert.True(transport.IsThrottled);
        Assert.NotNull(transport.ThrottledUntil);
        Assert.False(string.IsNullOrWhiteSpace(transport.ThrottleReason));

        // After recovery (200-OK): both null again.
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.EnsureRegisteredAsync();
        Assert.False(transport.IsThrottled);
        Assert.Null(transport.ThrottledUntil);
        Assert.Null(transport.ThrottleReason);
    }
}
