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
public sealed class GvSipWebSocketChannel : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _serverUri;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    public event EventHandler<SipMessageEventArgs>? MessageReceived;

    public GvSipWebSocketChannel(Uri serverUri, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentNullException.ThrowIfNull(logger);
        _serverUri = serverUri.ToString();
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol("sip");
        _ws.Options.SetRequestHeader("Origin", "https://voice.google.com");
        _ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");

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

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16384];
        var messageBuffer = new MemoryStream();

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
                    break;
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
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
#pragma warning disable CA1848, CA1873 // Debug/UAT tool — LoggerMessage perf not required
                _logger.LogWarning(ex, "WebSocket receive error");
#pragma warning restore CA1848, CA1873
                break;
            }
        }
    }

    public async Task CloseAsync()
    {
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
        _ws?.Dispose();
        _receiveCts?.Dispose();
    }
}
