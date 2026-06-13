namespace RotaryPhoneController.GVBridge.Sip;

/// <summary>
/// Abstraction over the SIP-over-WebSocket signaling channel (RFC 7118) so the
/// transport's keep-alive timing, reconnect-on-close, and status transitions can
/// be unit-tested without a live network (see <c>FakeSipWebSocketChannel</c> in tests).
/// Implemented in production by <see cref="GvSipWebSocketChannel"/>.
/// </summary>
public interface ISipWebSocketChannel : IDisposable
{
    /// <summary>Whether the underlying socket is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>Open the socket and start the receive loop.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Send a SIP message (or keep-alive ping) as a WebSocket Text frame.</summary>
    Task SendAsync(string sipMessage, CancellationToken ct = default);

    /// <summary>
    /// Send the RFC 5626 §3.5.1 double-CRLF (<c>"\r\n\r\n"</c>) keep-alive ping
    /// as a WebSocket Text frame (RFC 7118 §6 permits CRLF keep-alive over WS).
    /// </summary>
    Task SendPingAsync(CancellationToken ct = default);

    /// <summary>Intentionally close the socket (cancels the receive loop first).</summary>
    Task CloseAsync();

    /// <summary>Raised for each complete inbound SIP message.</summary>
    event EventHandler<SipMessageEventArgs>? MessageReceived;

    /// <summary>
    /// Raised exactly once when the receive loop exits. Carries whether the close
    /// was intentional (our own cancellation) or unexpected (server close / error).
    /// </summary>
    event EventHandler<WebSocketClosedEventArgs>? Closed;
}
