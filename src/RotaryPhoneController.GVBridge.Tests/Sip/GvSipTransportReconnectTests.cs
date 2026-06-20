using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Drives <see cref="GvSipTransport"/> against a <see cref="FakeSipWebSocketChannel"/>
/// to test keep-alive, reconnect, single-flight, auth escalation, status, and disposal
/// without any live network.
/// </summary>
public class GvSipTransportReconnectTests
{
    private static readonly SipCredentials TestCreds =
        new(SipUsername: "sip-token", BearerToken: "crypto-key", PhoneNumber: "+15551234567", ExpirySeconds: 3600);

    /// <summary>Build a REGISTER 200-OK that SIPSorcery parses and that carries keep=N in the Via.</summary>
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

    /// <summary>An auto-responder that feeds a 200-OK whenever a REGISTER is sent.</summary>
    private static Action<string, FakeSipWebSocketChannel> RegisterAutoResponder(int keep = 240) =>
        (payload, ch) =>
        {
            if (payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                ch.FeedMessage(Register200Ok(keep));
        };

    private static GvSipTransport CreateTransport(
        FakeSipWebSocketChannel fake,
        Func<Task<SipCredentials>>? getCreds = null,
        ReconnectOptions? options = null)
    {
        return new GvSipTransport(
            NullLogger<GvSipTransport>.Instance,
            getCreds ?? (() => Task.FromResult(TestCreds)),
            loggerFactory: null,
            channelFactory: (_, _) => fake,
            options: options);
    }

    /// <summary>Fast options for deterministic timing tests: ~1s keep-alive, ~1ms backoff base.</summary>
    private static ReconnectOptions FastOptions => new()
    {
        DefaultKeepAliveSeconds = 2,
        KeepAliveFloorSeconds = 0,
        BackoffScheduleSeconds = [0, 0, 0],
        BackoffJitterFraction = 0.0,
        RegisterTimeout = TimeSpan.FromSeconds(5),
        MinRegisterIntervalSeconds = 0, // no rate-floor in timing tests
    };

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
    public async Task EnsureRegistered_FeedsRegister200_BecomesRegistered()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = CreateTransport(fake);

        await transport.EnsureRegisteredAsync();

        Assert.True(transport.IsRegistered);
        Assert.True(transport.IsConnected);
        Assert.NotNull(transport.LastConnectedAt);
        Assert.Equal(1, fake.ConnectCount);
    }

    [Fact]
    public async Task KeepAlive_SendsDoubleCrlf()
    {
        // keep=2 with floor 0 -> ping every ~1s. Bounded wait well under the 3s limit.
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder(keep: 2) };
        await using var transport = CreateTransport(fake, options: FastOptions);

        await transport.EnsureRegisteredAsync();

        var sawPing = await WaitForAsync(() => fake.SawKeepAlivePing());
        Assert.True(sawPing, "expected a double-CRLF keep-alive ping to be sent");
        Assert.True(fake.PingCount >= 1);
    }

    [Fact]
    public async Task Reconnect_OnUnexpectedClose_ReRegisters()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = CreateTransport(fake, options: FastOptions);

        await transport.EnsureRegisteredAsync();
        Assert.Equal(1, fake.ConnectCount);

        // Simulate Google dropping the idle socket.
        fake.RaiseClosed(wasIntentional: false);

        var reconnected = await WaitForAsync(() => fake.ConnectCount >= 2 && transport.IsRegistered);
        Assert.True(reconnected, $"expected reconnect; connectCount={fake.ConnectCount}, registered={transport.IsRegistered}");
    }

    [Fact]
    public async Task Reconnect_OnIntentionalClose_DoesNotReconnect()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        await using var transport = CreateTransport(fake, options: FastOptions);

        await transport.EnsureRegisteredAsync();
        Assert.Equal(1, fake.ConnectCount);

        fake.RaiseClosed(wasIntentional: true);

        // Give any (wrongly-triggered) reconnect a chance to run.
        await Task.Delay(300);
        Assert.Equal(1, fake.ConnectCount);
    }

    [Fact]
    public async Task Reconnect_IsSingleFlight()
    {
        // Gate the REGISTER response so we can fire multiple Closed events while a
        // reconnect is in flight, then release and assert only ONE reconnect ran.
        var gate = new TaskCompletionSource();
        var firstRegisterDone = false;
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;
            if (!firstRegisterDone)
            {
                // First (initial) registration responds immediately.
                firstRegisterDone = true;
                ch.FeedMessage(Register200Ok());
            }
            else
            {
                // Reconnect registration: wait on the gate before responding so the
                // reconnect stays in-flight while we raise extra Closed events.
                gate.Task.ContinueWith(_ => ch.FeedMessage(Register200Ok()), TaskScheduler.Default);
            }
        };

        await using var transport = CreateTransport(fake, options: FastOptions);
        await transport.EnsureRegisteredAsync();
        Assert.Equal(1, fake.ConnectCount);

        // Fire several unexpected closes rapidly; the single-flight guard must collapse them.
        fake.RaiseClosed(wasIntentional: false);
        fake.RaiseClosed(wasIntentional: false);
        fake.RaiseClosed(wasIntentional: false);

        // Wait until exactly one reconnect connect has started.
        await WaitForAsync(() => fake.ConnectCount >= 2);
        await Task.Delay(200); // allow any erroneous extra reconnects to surface

        // Release the gated reconnect REGISTER.
        gate.SetResult();
        await WaitForAsync(() => transport.IsRegistered);

        // Exactly one reconnect (connect #2). No extra connects from the duplicate closes.
        Assert.Equal(2, fake.ConnectCount);
    }

    [Fact]
    public async Task ConcurrentEnsureRegisteredAndClose_IsSingleFlight()
    {
        // A Closed event (push reconnect) and a concurrent EnsureRegisteredAsync (pull path)
        // must funnel through the SAME single-flight register: exactly one new ConnectAsync,
        // not two parallel RegisterAsync racing TeardownChannelAsync/create-channel.
        var gate = new TaskCompletionSource();
        var registerCount = 0;
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;

            // First REGISTER (initial) responds immediately. Subsequent (reconnect/ensure)
            // REGISTERs wait on the gate so the attempt stays in-flight while we pile on a
            // concurrent EnsureRegisteredAsync.
            if (Interlocked.Increment(ref registerCount) == 1)
                ch.FeedMessage(Register200Ok());
            else
                gate.Task.ContinueWith(_ => ch.FeedMessage(Register200Ok()), TaskScheduler.Default);
        };

        await using var transport = CreateTransport(fake, options: FastOptions);
        await transport.EnsureRegisteredAsync();
        Assert.Equal(1, fake.ConnectCount);

        // Drop the socket so the next EnsureRegisteredAsync sees !IsConnected and forces a
        // re-register, AND kick the push reconnect via a Closed event — at the same time.
        await fake.CloseAsync(); // IsConnected -> false
        fake.RaiseClosed(wasIntentional: false);

        // Concurrent pull-path register while the push reconnect is in flight.
        var ensureTask = transport.EnsureRegisteredAsync();

        // Wait until the single (gated) reconnect connect has started, then allow time for
        // any erroneous SECOND connect to surface.
        await WaitForAsync(() => fake.ConnectCount >= 2);
        await Task.Delay(200);

        // Release the gated REGISTER so both the push loop and the pull ensure complete.
        gate.SetResult();
        await ensureTask;
        await WaitForAsync(() => transport.IsRegistered);

        // Exactly one reconnect connect (#2). The concurrent triggers joined one attempt.
        Assert.Equal(2, fake.ConnectCount);
        Assert.True(transport.IsRegistered);
    }

    [Fact]
    public async Task Status_ReflectsConnectAndDrop()
    {
        // First registration auto-responds; the reconnect attempt does NOT (its REGISTER
        // gets no 200-OK), so the transport stays down long enough to observe the transition.
        var allowResponse = true;
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (payload.StartsWith("REGISTER ", StringComparison.Ordinal) && allowResponse)
                ch.FeedMessage(Register200Ok());
        };

        await using var transport = CreateTransport(fake, options: new ReconnectOptions
        {
            DefaultKeepAliveSeconds = 2,
            KeepAliveFloorSeconds = 0,
            BackoffScheduleSeconds = [60],
            BackoffJitterFraction = 0.0,
            RegisterTimeout = TimeSpan.FromMilliseconds(300),
            MinRegisterIntervalSeconds = 0,
        });

        await transport.EnsureRegisteredAsync();
        Assert.True(transport.IsRegistered);
        Assert.True(transport.IsConnected);
        Assert.NotNull(transport.LastConnectedAt);

        // Stop responding so the reconnect can't complete, then drop the socket.
        allowResponse = false;
        await fake.CloseAsync(); // IsConnected -> false
        fake.RaiseClosed(wasIntentional: false);

        // Registration must read false the instant the drop is processed (honest status).
        var wentDown = await WaitForAsync(() => !transport.IsRegistered);
        Assert.True(wentDown, "IsRegistered should be false after an unexpected drop");
    }

    [Fact]
    public async Task AuthFailure_FiresEvent_OnPostDigest401()
    {
        var fake = new FakeSipWebSocketChannel();
        var authFiredCount = 0;
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;

            // First REGISTER (cseq 1, no auth) -> 401 challenge.
            // Second REGISTER (cseq 2, with Digest) -> 401 again = real post-Digest failure.
            var cseq = payload.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal) ? 1 : 2;
            ch.FeedMessage(Register401Challenge(cseq));
        };

        await using var transport = CreateTransport(fake, options: new ReconnectOptions
        {
            BackoffScheduleSeconds = [60], // don't loop fast during the assert
            BackoffJitterFraction = 0.0,
            RegisterTimeout = TimeSpan.FromSeconds(2),
            MinRegisterIntervalSeconds = 0,
        });
        transport.AuthenticationFailed += (_, _) => Interlocked.Increment(ref authFiredCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());

        Assert.True(authFiredCount >= 1, "AuthenticationFailed should fire on a post-Digest 401");
    }

    [Fact]
    public async Task Register603Declined_FiresAuthFailure_AndStaysUnregistered()
    {
        // Google returns 603 Declined to the Digest REGISTER (the live 2026-06-19 incident).
        // This is a real registration decline: it MUST escalate via AuthenticationFailed so the
        // recovery ladder runs, and the transport must NOT report itself registered.
        var fake = new FakeSipWebSocketChannel();
        var authFiredCount = 0;
        fake.OnSend = (payload, ch) =>
        {
            if (!payload.StartsWith("REGISTER ", StringComparison.Ordinal))
                return;

            // First REGISTER (cseq 1, no auth) -> 401 challenge.
            // Second REGISTER (cseq 2, with Digest) -> 603 Declined (policy rejection).
            var cseq = payload.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal) ? 1 : 2;
            ch.FeedMessage(cseq == 1 ? Register401Challenge(1) : Register603Declined(2));
        };

        await using var transport = CreateTransport(fake, options: new ReconnectOptions
        {
            BackoffScheduleSeconds = [60], // don't loop fast during the assert
            BackoffJitterFraction = 0.0,
            RegisterTimeout = TimeSpan.FromSeconds(2),
            MinRegisterIntervalSeconds = 0,
        });
        transport.AuthenticationFailed += (_, _) => Interlocked.Increment(ref authFiredCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureRegisteredAsync());

        Assert.True(authFiredCount >= 1, "AuthenticationFailed should fire on a 603 Declined so recovery runs");
        Assert.False(transport.IsRegistered, "IsRegistered must be false after a 603 Declined");
    }

    [Fact]
    public async Task RegisterRateFloor_SpacesConsecutiveAttempts()
    {
        // Storm guard: even with zero backoff, consecutive REGISTER attempts must be spaced by
        // MinRegisterIntervalSeconds. The first attempt is never delayed; the reconnect attempt is.
        var registerTimes = new List<long>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fake = new FakeSipWebSocketChannel();
        fake.OnSend = (payload, ch) =>
        {
            if (payload.StartsWith("REGISTER ", StringComparison.Ordinal) &&
                payload.Contains("CSeq: 1 REGISTER", StringComparison.Ordinal))
            {
                registerTimes.Add(sw.ElapsedMilliseconds);
                ch.FeedMessage(Register200Ok());
            }
        };

        await using var transport = CreateTransport(fake, options: new ReconnectOptions
        {
            DefaultKeepAliveSeconds = 2,
            KeepAliveFloorSeconds = 0,
            BackoffScheduleSeconds = [0], // isolate the floor from backoff
            BackoffJitterFraction = 0.0,
            RegisterTimeout = TimeSpan.FromSeconds(5),
            MinRegisterIntervalSeconds = 0.5, // 500ms hard floor
        });

        await transport.EnsureRegisteredAsync();   // 1st attempt — not delayed
        Assert.Single(registerTimes);

        fake.RaiseClosed(wasIntentional: false);   // -> reconnect -> 2nd attempt (floored)

        await WaitForAsync(() => registerTimes.Count >= 2);
        Assert.True(registerTimes.Count >= 2, "expected a reconnect REGISTER");

        var gap = registerTimes[1] - registerTimes[0];
        Assert.True(gap >= 400, $"expected ~500ms rate-floor between attempts, got {gap}ms");
    }

    [Fact]
    public async Task PlainDrop_DoesNotFireAuthFailure()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        var authFired = false;
        await using var transport = CreateTransport(fake, options: FastOptions);
        transport.AuthenticationFailed += (_, _) => authFired = true;

        await transport.EnsureRegisteredAsync();
        fake.RaiseClosed(wasIntentional: false); // plain network drop

        await WaitForAsync(() => fake.ConnectCount >= 2);
        await Task.Delay(200);

        Assert.False(authFired, "a plain network drop must not trigger an auth failure / cookie refresh");
    }

    [Fact]
    public async Task DisposeAsync_StopsKeepAliveAndReconnect()
    {
        var fake = new FakeSipWebSocketChannel { OnSend = RegisterAutoResponder() };
        var transport = CreateTransport(fake, options: FastOptions);

        await transport.EnsureRegisteredAsync();
        Assert.Equal(1, fake.ConnectCount);

        await transport.DisposeAsync();

        var connectsAfterDispose = fake.ConnectCount;
        var pingsAfterDispose = fake.PingCount;

        // Raising Closed on the (now-detached) channel must not start a new connect.
        fake.RaiseClosed(wasIntentional: false);
        await Task.Delay(300);

        Assert.Equal(connectsAfterDispose, fake.ConnectCount);
        // No further pings after dispose (timer stopped).
        await Task.Delay(200);
        Assert.Equal(pingsAfterDispose, fake.PingCount);
    }

    /// <summary>A 603 Declined response with the given CSeq number that SIPSorcery parses.</summary>
    private static string Register603Declined(int cseq) =>
        "SIP/2.0 603 Declined\r\n" +
        "Via: SIP/2.0/WSS abc123.invalid;branch=z9hG4bK-test\r\n" +
        "To: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=server-tag\r\n" +
        "From: <sip:sip-token@web.c.pbx.voice.sip.google.com>;tag=client-tag\r\n" +
        "Call-ID: test-call-id\r\n" +
        $"CSeq: {cseq} REGISTER\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    /// <summary>A 401 challenge with the given CSeq number that SIPSorcery parses.</summary>
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
}
