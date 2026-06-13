using System.Collections.Concurrent;
using RotaryPhoneController.GVBridge.Sip;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Test double for <see cref="ISipWebSocketChannel"/> that lets tests:
///  - count connects (assert reconnect attempts),
///  - capture every <see cref="SendAsync"/> / <see cref="SendPingAsync"/> payload
///    (assert the double-CRLF keep-alive ping was sent, inspect REGISTER, etc.),
///  - feed canned inbound SIP via <see cref="FeedMessage"/> (REGISTER 200-OK, etc.),
///  - raise <see cref="Closed"/> on demand with a chosen WasIntentional flag.
/// No real network is involved.
/// </summary>
internal sealed class FakeSipWebSocketChannel : ISipWebSocketChannel
{
    private readonly ConcurrentQueue<string> _sends = new();
    private int _connectCount;
    private int _pingCount;

    public int ConnectCount => Volatile.Read(ref _connectCount);
    public int PingCount => Volatile.Read(ref _pingCount);

    public IReadOnlyList<string> Sends => _sends.ToArray();

    public bool IsConnected { get; private set; }

    /// <summary>If set, the next SendAsync/SendPingAsync throws to simulate a dead link.</summary>
    public bool FailNextSend { get; set; }

    /// <summary>
    /// Optional hook invoked for every outbound payload (after it is captured).
    /// Tests use this to auto-feed a canned REGISTER 200-OK in response to the
    /// REGISTER the transport sends, so the in-flight RegisterAsync completes.
    /// </summary>
    public Action<string, FakeSipWebSocketChannel>? OnSend { get; set; }

    public event EventHandler<SipMessageEventArgs>? MessageReceived;
    public event EventHandler<WebSocketClosedEventArgs>? Closed;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _connectCount);
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(string sipMessage, CancellationToken ct = default)
    {
        if (FailNextSend)
        {
            FailNextSend = false;
            throw new InvalidOperationException("simulated send failure");
        }

        _sends.Enqueue(sipMessage);
        OnSend?.Invoke(sipMessage, this);
        return Task.CompletedTask;
    }

    public Task SendPingAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pingCount);
        return SendAsync(GvSipWebSocketChannel.DoubleCrlfPing, ct);
    }

    public Task CloseAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void Dispose() => IsConnected = false;

    // --- test drivers ---

    /// <summary>Deliver a canned inbound SIP message to subscribers.</summary>
    public void FeedMessage(string message) =>
        MessageReceived?.Invoke(this, new SipMessageEventArgs(message));

    /// <summary>Raise the Closed event (e.g. simulate a server-side drop).</summary>
    public void RaiseClosed(bool wasIntentional) =>
        Closed?.Invoke(this, new WebSocketClosedEventArgs(wasIntentional, wasIntentional ? "intentional" : "dropped"));

    /// <summary>Whether any captured send equals the double-CRLF keep-alive ping.</summary>
    public bool SawKeepAlivePing() => _sends.Contains(GvSipWebSocketChannel.DoubleCrlfPing);
}
