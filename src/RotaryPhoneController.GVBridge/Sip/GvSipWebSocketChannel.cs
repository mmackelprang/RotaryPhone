using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Sip;

public sealed class SipMessageEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

/// <summary>
/// Custom SIP-over-WebSocket channel that bypasses SSL certificate validation.
/// Needed because Google's SIP proxy at 216.239.36.145 serves a cert
/// for a Google domain, but we connect by raw IP address.
///
/// This replaces SIPSorcery's SIPClientWebSocketChannel which doesn't
/// expose certificate validation options.
/// </summary>
public sealed class GvSipWebSocketChannel : ISipWebSocketChannel
{
    /// <summary>
    /// The RFC 5626 §3.5.1 client keep-alive ping: double-CRLF, sent as a
    /// WebSocket Text frame (RFC 7118 §6 permits the CRLF technique over WS).
    /// Google's expected response is a single-CRLF ("\r\n") pong.
    /// </summary>
    public const string DoubleCrlfPing = "\r\n\r\n";

    private readonly ILogger _logger;
    private readonly string _serverUri;
    private readonly TimeSpan _protocolKeepAliveInterval;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    // Set true only when WE cancel the receive loop (CloseAsync / reconnect / dispose),
    // so the Closed event can distinguish an intentional close from a server-side drop.
    private volatile bool _intentionalClose;
    private int _closedRaised; // Interlocked guard: raise Closed at most once per loop

    public event EventHandler<SipMessageEventArgs>? MessageReceived;

    public event EventHandler<WebSocketClosedEventArgs>? Closed;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public GvSipWebSocketChannel(Uri serverUri, ILogger logger)
        : this(serverUri, logger, TimeSpan.FromSeconds(120))
    {
    }

    /// <param name="serverUri">wss:// endpoint.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="protocolKeepAliveInterval">
    /// Secondary, transport-level keep-alive (<see cref="ClientWebSocket.Options"/>
    /// KeepAliveInterval). Defense-in-depth behind the primary app-level double-CRLF
    /// ping that the transport drives. Defaults to 120s.
    /// </param>
    public GvSipWebSocketChannel(Uri serverUri, ILogger logger, TimeSpan protocolKeepAliveInterval)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentNullException.ThrowIfNull(logger);
        _serverUri = serverUri.ToString();
        _logger = logger;
        _protocolKeepAliveInterval = protocolKeepAliveInterval > TimeSpan.Zero
            ? protocolKeepAliveInterval
            : TimeSpan.FromSeconds(120);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _intentionalClose = false;
        Interlocked.Exchange(ref _closedRaised, 0);

        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol("sip");
        _ws.Options.SetRequestHeader("Origin", "https://voice.google.com");
        _ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");

        // Secondary keep-alive (RFC 6455 §5.5.2 protocol PING frames). The primary
        // keep-alive is the app-level double-CRLF ping driven by GvSipTransport;
        // this is cheap defense-in-depth insurance.
        _ws.Options.KeepAliveInterval = _protocolKeepAliveInterval;

        // Try without compression first — server may not require it

#pragma warning disable CA1848, CA1873 // Debug/UAT tool
        _logger.LogInformation("Connecting WebSocket to {Uri}...", _serverUri);
#pragma warning restore CA1848, CA1873

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        await _ws.ConnectAsync(new Uri(_serverUri), timeoutCts.Token).ConfigureAwait(false);

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("WebSocket connected! State={State}", _ws.State);
#pragma warning restore CA1848, CA1873

        // Start receive loop
        _receiveCts = new CancellationTokenSource();
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
    }

    public async Task SendAsync(string sipMessage, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket not connected");

        var bytes = Encoding.UTF8.GetBytes(sipMessage);
        // RFC 7118 allows Text or Binary — use Text for requests (matching browser TsSIP)
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send the RFC 5626 §3.5.1 double-CRLF keep-alive ping. Throws if the socket
    /// is not open so the caller (the transport's keep-alive timer) can treat a
    /// failed ping as the earliest drop signal and trigger reconnect.
    /// </summary>
    public Task SendPingAsync(CancellationToken ct = default) => SendAsync(DoubleCrlfPing, ct);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16384];
        var messageBuffer = new MemoryStream();
        var intentional = false;
        string? closeDescription = null;

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
#pragma warning disable CA1848, CA1873
                    _logger.LogInformation("WebSocket closed by server: {Status} {Desc}",
                        _ws.CloseStatus, _ws.CloseStatusDescription);
#pragma warning restore CA1848, CA1873
                    closeDescription = _ws.CloseStatusDescription;
                    break; // server-initiated close — unexpected
                }

                messageBuffer.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage)
                {
                    continue; // accumulate until complete message
                }

                // Process complete message
                var messageBytes = messageBuffer.ToArray();
                messageBuffer.SetLength(0); // reset for next message

#pragma warning disable CA1848, CA1873
                _logger.LogDebug("WebSocket received: type={Type} size={Size} bytes",
                    result.MessageType, messageBytes.Length);
#pragma warning restore CA1848, CA1873

                var message = Encoding.UTF8.GetString(messageBytes);
                if (!string.IsNullOrEmpty(message))
                {
                    MessageReceived?.Invoke(this, new SipMessageEventArgs(message));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // We cancelled the loop (CloseAsync / reconnect / dispose) — intentional.
                intentional = true;
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
#pragma warning disable CA1848, CA1873 // Debug/UAT tool — LoggerMessage perf not required
                _logger.LogWarning(ex, "WebSocket receive error");
#pragma warning restore CA1848, CA1873
                closeDescription = ex.Message;
                break; // receive error (e.g. remote party closed) — unexpected
            }
        }

        // If the loop exited because ct was cancelled (without throwing), treat as intentional.
        if (ct.IsCancellationRequested)
            intentional = true;

        // _intentionalClose is set by CloseAsync before it cancels the token.
        if (_intentionalClose)
            intentional = true;

        RaiseClosed(intentional, closeDescription);
    }

    private void RaiseClosed(bool wasIntentional, string? description)
    {
        // Fire exactly once per dead loop.
        if (Interlocked.Exchange(ref _closedRaised, 1) != 0)
            return;

        Closed?.Invoke(this, new WebSocketClosedEventArgs(wasIntentional, description));
    }

    /// <summary>
    /// Test-only hook to raise the <see cref="Closed"/> event without a live socket.
    /// </summary>
    internal void RaiseClosedForTest(bool wasIntentional, string? description) =>
        Closed?.Invoke(this, new WebSocketClosedEventArgs(wasIntentional, description));

    public async Task CloseAsync()
    {
        // Mark intentional BEFORE cancelling so the receive loop reports the right reason.
        _intentionalClose = true;

        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch { /* best effort */ }
#pragma warning restore CA1031
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _receiveCts?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intentionalClose = true;
        _ws?.Dispose();
        _receiveCts?.Dispose();
    }
}
