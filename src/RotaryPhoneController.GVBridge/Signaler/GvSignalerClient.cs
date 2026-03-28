using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Signaler;

public class GvSignalerClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<GvSignalerClient> _logger;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private string? _sid;
    private string? _gsessionId;
    private int _lastAid;

    public event Action<IncomingCallEvent>? OnIncomingCall;
    public event Action<CallEndedEvent>? OnCallEnded;
    public event Action<SmsReceivedEvent>? OnSmsReceived;
    public bool IsConnected { get; private set; }

    public GvSignalerClient(HttpClient http, string baseUrl, ILogger<GvSignalerClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var serverUrl = await ChooseServerAsync(ct);
            await CreateChannelAsync(serverUrl, ct);
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pollTask = PollLoopAsync(serverUrl, _pollCts.Token);
            IsConnected = true;
            _logger.LogInformation("Signaler connected (SID={Sid})", _sid?[..Math.Min(_sid?.Length ?? 0, 8)]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect signaler");
            IsConnected = false;
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_pollCts != null)
        {
            await _pollCts.CancelAsync();
            if (_pollTask != null)
            {
                try { await _pollTask; } catch (OperationCanceledException) { }
            }
            _pollCts.Dispose();
            _pollCts = null;
        }
        IsConnected = false;
        _logger.LogInformation("Signaler disconnected");
    }

    private async Task<string> ChooseServerAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/punctual/v1/chooseServer";
        var response = await _http.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? _baseUrl : body.Trim();
    }

    private async Task CreateChannelAsync(string serverUrl, CancellationToken ct)
    {
        var url = $"{serverUrl}/punctual/multi-watch/channel?VER=8&CVER=22&RID=rpc&t=1";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        try
        {
            var doc = JsonDocument.Parse(body);
            _sid = ExtractSessionParam(doc, "SID");
            _gsessionId = ExtractSessionParam(doc, "gsessionid");
            _lastAid = 0;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse channel creation response, using raw parsing");
            _sid = "unknown";
            _gsessionId = "unknown";
        }
    }

    private async Task PollLoopAsync(string serverUrl, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        var maxRetryDelay = TimeSpan.FromSeconds(30);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var url = $"{serverUrl}/punctual/multi-watch/channel" +
                        $"?VER=8&CVER=22&RID=rpc&SID={_sid}&gsessionid={_gsessionId}" +
                        $"&AID={_lastAid}&TYPE=xmlhttp&CI=0";

                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Signaler poll returned {Status}", response.StatusCode);
                        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                            response.StatusCode == System.Net.HttpStatusCode.Gone)
                        {
                            _logger.LogInformation("Signaler session expired, reconnecting");
                            await CreateChannelAsync(serverUrl, ct);
                        }
                        await Task.Delay(retryDelay, ct);
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelay.TotalSeconds));
                        continue;
                    }

                    retryDelay = TimeSpan.FromSeconds(1);
                    var body = await response.Content.ReadAsStringAsync(ct);

                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var events = ParseSignalerResponse(body);
                        foreach (var evt in events)
                        {
                            switch (evt)
                            {
                                case IncomingCallEvent ic:
                                    _logger.LogInformation("Signaler: incoming call from {Number}", ic.CallerNumber);
                                    OnIncomingCall?.Invoke(ic);
                                    break;
                                case CallEndedEvent ce:
                                    _logger.LogInformation("Signaler: call {CallId} ended", ce.CallId);
                                    OnCallEnded?.Invoke(ce);
                                    break;
                                case SmsReceivedEvent sms:
                                    _logger.LogInformation("Signaler: SMS from {From}", sms.From);
                                    OnSmsReceived?.Invoke(sms);
                                    break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Signaler poll error, retrying in {Delay}", retryDelay);
                    await Task.Delay(retryDelay, ct);
                    retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelay.TotalSeconds));
                }
            }
        }
        finally
        {
            IsConnected = false;
            _logger.LogInformation("Signaler poll loop exited");
        }
    }

    private List<SignalerEvent> ParseSignalerResponse(string body)
    {
        var events = new List<SignalerEvent>();
        try
        {
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!line.StartsWith('['))
                    continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                    continue;
                for (int i = 0; i < root.GetArrayLength(); i++)
                {
                    var item = root[i];
                    var evt = ClassifyEvent(item);
                    if (evt != null)
                    {
                        events.Add(evt);
                        if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0 &&
                            item[0].ValueKind == JsonValueKind.Number)
                        {
                            _lastAid = item[0].GetInt32();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Signaler response parse error (raw: {Body})",
                body.Length > 500 ? body[..500] : body);
        }
        return events;
    }

    private SignalerEvent? ClassifyEvent(JsonElement item)
    {
        // Serialize once for pattern matching
        var text = item.ToString();
        if (string.IsNullOrEmpty(text) || text.Length < 5)
            return null;

        // Incoming call: SDP offer from Google's "xavier" SIP UA
        if (text.Contains("o=xavier", StringComparison.Ordinal))
        {
            var callerNumber = ExtractCallerNumber(item) ?? "Unknown";
            return new IncomingCallEvent(
                CallId: $"gv-{Guid.NewGuid():N}",
                CallerNumber: callerNumber);
        }

        // Only match specific GV event type indicators, not generic words
        // TODO: Refine with actual signaler payloads from live API testing
        if (text.Contains("\"call_ended\"", StringComparison.OrdinalIgnoreCase))
        {
            var callId = ExtractCallerNumber(item) ?? "";
            return new CallEndedEvent(CallId: callId);
        }

        if (text.Contains("\"sms_received\"", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"INCOMING_TEXT\"", StringComparison.Ordinal))
        {
            return new SmsReceivedEvent(
                From: ExtractCallerNumber(item) ?? "Unknown",
                Body: "(content pending live API format discovery)",
                ThreadId: "");
        }

        return null;
    }

    private static string? ExtractCallerNumber(JsonElement element)
    {
        var text = element.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\+\d{10,15}");
        return match.Success ? match.Value : null;
    }

    private static string? ExtractSessionParam(JsonDocument doc, string paramName)
    {
        var text = doc.RootElement.ToString();
        var key = $"\"{paramName}\"";
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var valueStart = text.IndexOf('"', idx + key.Length);
        if (valueStart < 0) return null;
        var valueEnd = text.IndexOf('"', valueStart + 1);
        if (valueEnd < 0) return null;
        return text[(valueStart + 1)..valueEnd];
    }
}
