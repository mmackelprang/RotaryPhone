using System.Net;
using System.Net.Sockets;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Diagnostics;

namespace RotaryPhoneController.Core;

public class SIPSorceryAdapter : ISipAdapter
{
    private readonly ILogger _logger;
    private SIPTransport? _sipTransport;
    private SIPUserAgent? _userAgent;
    private readonly string _localIPAddress;
    private readonly int _localPort;
    private SIPRequest? _pendingInviteRequest;
    private bool _inviteAnswered;

    public event Action<bool>? OnHookChange;
    public event Action<string>? OnDigitsReceived;
    public event Action? OnIncomingCall;
    public event Action<SipMessageEntry>? OnSipMessageLogged;

    /// <summary>
    /// Fired when HT801 responds with 200 OK containing SDP.
    /// Parameters: (negotiated RTP port, negotiated IP address).
    /// </summary>
    public event Action<int, string>? OnRtpDetailsNegotiated;

    /// <summary>
    /// Gets whether the SIP transport is currently listening
    /// </summary>
    public bool IsListening => _sipTransport != null;

    public SIPSorceryAdapter(ILogger logger, AppConfiguration config)
    {
        _logger = logger;
        _localIPAddress = config.SipListenAddress;
        _localPort = config.SipPort;
    }

    public SIPSorceryAdapter(ILogger logger, string localIPAddress = "0.0.0.0", int localPort = 5060)
    {
        _logger = logger;
        _localIPAddress = localIPAddress;
        _localPort = localPort;
    }

    public void StartListening()
    {
        try
        {
            _logger.Information("Starting SIP transport on {IP}:{Port}", _localIPAddress, _localPort);

            // Create SIP transport
            _sipTransport = new SIPTransport();
            
            // Add UDP listener on the specified IP and port
            var listenEndpoint = new IPEndPoint(IPAddress.Parse(_localIPAddress), _localPort);
            _sipTransport.AddSIPChannel(new SIPUDPChannel(listenEndpoint));

            // Subscribe to SIP events
            _sipTransport.SIPTransportRequestReceived += OnSIPRequestReceived;
            _sipTransport.SIPTransportResponseReceived += OnSIPResponseReceived;

            _logger.Information("SIP transport started successfully");

            // Initialize SIPUserAgent
            _userAgent = new SIPUserAgent(_sipTransport, null);
            _logger.Information("SIPUserAgent initialized");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start SIP transport");
            throw;
        }
    }

    private void LogSipMessage(SipDirection direction, string method, string from, string to,
        int? statusCode = null, string? statusText = null, string? callId = null, string? note = null)
    {
        OnSipMessageLogged?.Invoke(new SipMessageEntry(
            DateTime.UtcNow, direction, method, from, to, statusCode, statusText, note, callId));
    }

    private Task<SocketError> OnSIPRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        _logger.Information("SIP Request received: {Method} from {RemoteEndPoint}", sipRequest.Method, remoteEndPoint);
        _logger.Debug("SIP Request details: {Request}", sipRequest.ToString());

        try
        {
            switch (sipRequest.Method)
            {
                case SIPMethodsEnum.NOTIFY:
                    HandleNotify(sipRequest);
                    LogSipMessage(SipDirection.Received, "NOTIFY", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);
                    break;
                case SIPMethodsEnum.INFO:
                    HandleInfo(sipRequest);
                    LogSipMessage(SipDirection.Received, "INFO", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);
                    break;
                case SIPMethodsEnum.INVITE:
                    HandleInvite(sipRequest);
                    LogSipMessage(SipDirection.Received, "INVITE", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);
                    break;
                case SIPMethodsEnum.BYE:
                    HandleBye(sipRequest);
                    LogSipMessage(SipDirection.Received, "BYE", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);
                    break;
                case SIPMethodsEnum.REGISTER:
                    HandleRegister(sipRequest, localSIPEndPoint, remoteEndPoint);
                    LogSipMessage(SipDirection.Received, "REGISTER", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);
                    break;
                case SIPMethodsEnum.OPTIONS:
                    HandleOptions(sipRequest);
                    LogSipMessage(SipDirection.Received, "OPTIONS", remoteEndPoint.ToString(), localSIPEndPoint.ToString(), callId: sipRequest.Header.CallId);
                    break;
                default:
                    _logger.Debug("Unhandled SIP method: {Method}", sipRequest.Method);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing SIP request");
        }
        
        return Task.FromResult(SocketError.Success);
    }

    private Task<SocketError> OnSIPResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
    {
        _logger.Information("SIP Response received: {StatusCode} {ReasonPhrase} from {RemoteEndPoint}",
            sipResponse.StatusCode, sipResponse.ReasonPhrase, remoteEndPoint);
        _logger.Debug("SIP Response details: {Response}", sipResponse.ToString());

        LogSipMessage(SipDirection.Received, sipResponse.Header.CSeqMethod.ToString(),
            remoteEndPoint.ToString(), localSIPEndPoint.ToString(),
            sipResponse.StatusCode, sipResponse.ReasonPhrase,
            callId: sipResponse.Header.CallId);

        // Detect HT801 answering our INVITE (user lifted handset while ringing)
        if (sipResponse.StatusCode == 200 && _pendingInviteRequest != null &&
            sipResponse.Header.CallId == _pendingInviteRequest.Header.CallId)
        {
            _logger.Information("HT801 answered INVITE (200 OK) — handset lifted, triggering off-hook");

            // Send ACK to complete the SIP dialog (required by SIP spec)
            try
            {
                var ackRequest = SIPRequest.GetRequest(
                    SIPMethodsEnum.ACK,
                    _pendingInviteRequest.URI,
                    sipResponse.Header.To,
                    _pendingInviteRequest.Header.From);

                ackRequest.Header.CallId = sipResponse.Header.CallId;
                ackRequest.Header.CSeq = sipResponse.Header.CSeq;
                ackRequest.Header.CSeqMethod = SIPMethodsEnum.ACK;
                ackRequest.Header.Contact = _pendingInviteRequest.Header.Contact;

                var targetEndpoint = sipResponse.RemoteSIPEndPoint
                    ?? SIPEndPoint.ParseSIPEndPoint($"{_pendingInviteRequest.URI.Host}:5060");
                _sipTransport?.SendRequestAsync(targetEndpoint, ackRequest);
                _logger.Information("ACK sent to HT801");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to send ACK");
            }

            // Parse RTP details from the HT801's SDP answer so the audio bridge
            // knows which port/IP the HT801 is actually listening on.
            if (!string.IsNullOrEmpty(sipResponse.Body))
            {
                var (rtpPort, rtpIp) = ExtractRtpDetailsFromSdp(sipResponse.Body);
                _logger.Information("Extracted RTP details from HT801 200 OK SDP: {IP}:{Port}", rtpIp, rtpPort);
                OnRtpDetailsNegotiated?.Invoke(rtpPort, rtpIp);
            }

            _inviteAnswered = true;
            OnHookChange?.Invoke(true);
        }

        return Task.FromResult(SocketError.Success);
    }

    private void HandleNotify(SIPRequest sipRequest)
    {
        _logger.Information("Processing NOTIFY message");
        
        var body = sipRequest.Body;
        if (!string.IsNullOrEmpty(body))
        {
            _logger.Debug("NOTIFY body: {Body}", body);
            
            // Check for hook state changes
            if (body.Contains("hook", StringComparison.OrdinalIgnoreCase))
            {
                bool isOffHook = body.Contains("off-hook", StringComparison.OrdinalIgnoreCase) || 
                                body.Contains("offhook", StringComparison.OrdinalIgnoreCase);
                _logger.Information("Hook state change detected: {State}", isOffHook ? "OFF-HOOK" : "ON-HOOK");
                OnHookChange?.Invoke(isOffHook);
            }
            
            // Check for dialed digits
            if (body.Contains("digit", StringComparison.OrdinalIgnoreCase) || 
                body.Contains("number", StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract the dialed number
                var number = ExtractDialedNumber(body);
                if (!string.IsNullOrEmpty(number))
                {
                    _logger.Information("Digits received: {Number}", number);
                    OnDigitsReceived?.Invoke(number);
                }
            }
        }
    }

    private void HandleInfo(SIPRequest sipRequest)
    {
        _logger.Information("Processing INFO message");
        
        var body = sipRequest.Body;
        if (!string.IsNullOrEmpty(body))
        {
            _logger.Debug("INFO body: {Body}", body);
            
            // HT801 may send dialed digits via INFO
            var number = ExtractDialedNumber(body);
            if (!string.IsNullOrEmpty(number))
            {
                _logger.Information("Digits received via INFO: {Number}", number);
                OnDigitsReceived?.Invoke(number);
            }
        }
    }

    private void HandleInvite(SIPRequest sipRequest)
    {
        _logger.Information("Processing INVITE from {Remote}", sipRequest.RemoteSIPEndPoint);

        // In this architecture, if the SIP Adapter (Server) receives an INVITE,
        // it means the HT801 (Client) is trying to place an outgoing call.
        // The dialed number is in the To header.
        //
        // The HT801 sends TWO INVITEs per outbound call:
        //   1. sip:rotaryphone@... — its own registration name (not a real number)
        //   2. sip:9193718044@...  — the actual dialed number
        // We must answer BOTH with 200 OK (to complete the SIP dialog and stop
        // retransmissions), but only fire OnDigitsReceived / OnHookChange for the
        // real dialed-number INVITE. Otherwise the first INVITE pushes the call
        // state machine into InCall before the real number arrives, and the digits
        // get ignored.

        try
        {
            var dialedNumber = sipRequest.Header.To.ToURI.User;
            if (string.IsNullOrEmpty(dialedNumber))
            {
                _logger.Warning("Received INVITE without a user in To header");
                return;
            }

            // Always answer the INVITE to establish the SIP dialog and stop
            // the HT801 from retransmitting.
            var response = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);

            // Resolve actual local IP when listening on 0.0.0.0 — the SDP must
            // contain a routable IP so HT801 knows where to send its RTP.
            // Without this, the SDP has c=IN IP4 0.0.0.0 which is a hold indicator
            // (RFC 3264) and causes one-way audio (HT801 -> us fails).
            var localIP = _localIPAddress;
            if (localIP == "0.0.0.0" && sipRequest.RemoteSIPEndPoint != null)
            {
                localIP = GetLocalIPForTarget(sipRequest.RemoteSIPEndPoint.Address.ToString());
                _logger.Information("Resolved local SDP address to {LocalIP} for INVITE response", localIP);
            }

            // Add Contact header to response (required)
            response.Header.Contact = new List<SIPContactHeader>
            {
                new SIPContactHeader(null, new SIPURI(SIPSchemesEnum.sip,
                    SIPEndPoint.ParseSIPEndPoint($"{localIP}:{_localPort}")))
            };

            // Add SDP to response (negotiate codec)
            // Use the same RTP port the audio bridge will bind (49000) so HT801
            // sends its RTP to the correct destination once the bridge starts.
            var localEndpoint = new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Parse(localIP), 49000);
            response.Body = CreateBasicSDP(localEndpoint);

            _logger.Information("INVITE 200 OK SDP: RTP at {IP}:{Port}", localIP, 49000);

            _sipTransport?.SendResponseAsync(response);

            // Only trigger call-flow events for real phone numbers, not the HT801's
            // registration name ("rotaryphone") or other non-numeric URI users.
            bool isRealNumber = IsDialableNumber(dialedNumber);

            if (isRealNumber)
            {
                _logger.Information("User dialed: {Number}", dialedNumber);
                OnDigitsReceived?.Invoke(dialedNumber);
                OnHookChange?.Invoke(true);
            }
            else
            {
                _logger.Information(
                    "Answered INVITE for non-dialable URI user '{User}' (HT801 registration) " +
                    "— SIP dialog established but no call-state change fired",
                    dialedNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing INVITE");
        }
    }

    /// <summary>
    /// Returns true if the URI user part looks like a real dialable number
    /// (digits, +, *, # only). Returns false for registration names like
    /// "rotaryphone" or other non-numeric strings.
    /// </summary>
    internal static bool IsDialableNumber(string uriUser)
    {
        if (string.IsNullOrEmpty(uriUser))
            return false;

        return uriUser.All(c => char.IsDigit(c) || c == '+' || c == '*' || c == '#');
    }

    private void HandleBye(SIPRequest sipRequest)
    {
        _logger.Information("Processing BYE message - Call terminated by remote party");

        // Send 200 OK to acknowledge the BYE (required by SIP spec)
        var response = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        _sipTransport?.SendResponseAsync(response);

        // Fire OnHookChange with false (on-hook) to simulate hanging up
        OnHookChange?.Invoke(false);
    }

    private void HandleOptions(SIPRequest sipRequest)
    {
        // Respond to OPTIONS keepalives with 200 OK
        var response = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        _sipTransport?.SendResponseAsync(response);
    }

    private void HandleRegister(SIPRequest sipRequest, SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint)
    {
        try
        {
            var contact = sipRequest.Header.Contact?.FirstOrDefault();
            // Use the requested expiry (default 3600) — the HT801 re-registers at
            // ~50% of this value. Too-short values cause REGISTER spam.
            var expires = sipRequest.Header.Expires > 0 ? sipRequest.Header.Expires : 3600;

            _logger.Debug("Processing REGISTER from {Remote}, Contact={Contact}, Expires={Expires}",
                remoteEndPoint, contact?.ContactURI, expires);

            var response = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
            response.Header.Expires = expires;

            if (contact != null)
            {
                // Set expires on the Contact header — some devices require this
                contact.Expires = expires;
                response.Header.Contact = new List<SIPContactHeader> { contact };
            }

            _sipTransport?.SendResponseAsync(response);
            _logger.Debug("REGISTER accepted — HT801 at {Remote} registered for {Expires}s", remoteEndPoint, expires);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing REGISTER");
        }
    }

    private string? ExtractDialedNumber(string body)
    {
        // Simple extraction logic - can be enhanced based on actual HT801 message format
        // Looking for patterns like "number=1234567890" or similar
        
        var lines = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("="))
            {
                var parts = line.Split('=');
                if (parts.Length == 2 && 
                    (parts[0].Trim().Contains("number", StringComparison.OrdinalIgnoreCase) ||
                     parts[0].Trim().Contains("digit", StringComparison.OrdinalIgnoreCase)))
                {
                    var number = parts[1].Trim();
                    // Basic validation - check if it's a number
                    if (number.All(c => char.IsDigit(c) || c == '+' || c == '-'))
                    {
                        return number;
                    }
                }
            }
        }
        
        return null;
    }

    public void SendInviteToHT801(string extensionToRing, string targetIP, int localRtpPort = 49000)
    {
        try
        {
            _logger.Information("Sending INVITE to HT801 at {IP} for extension {Extension}", targetIP, extensionToRing);

            if (_sipTransport == null)
            {
                _logger.Error("SIP transport is not initialized. Call StartListening() first.");
                return;
            }

            // Resolve actual local IP when listening on 0.0.0.0
            var localIP = _localIPAddress;
            if (localIP == "0.0.0.0")
            {
                localIP = GetLocalIPForTarget(targetIP);
                _logger.Information("Resolved local SIP address to {LocalIP} for target {TargetIP}", localIP, targetIP);
            }

            // Create destination URI
            var destinationUri = SIPURI.ParseSIPURIRelaxed($"sip:{extensionToRing}@{targetIP}");

            // Create local SIP endpoint
            // Use a distinct caller ID — NOT the same extension as the target.
            // If From and To have the same user, the HT801 may drop the INVITE as a loop.
            var fromHeader = new SIPFromHeader("RotaryPhone Controller",
                new SIPURI("rotaryphone", localIP, null, SIPSchemesEnum.sip, SIPProtocolsEnum.udp),
                CallProperties.CreateNewTag());

            // Create the INVITE request
            var inviteRequest = SIPRequest.GetRequest(
                SIPMethodsEnum.INVITE,
                destinationUri,
                new SIPToHeader(null, destinationUri, null),
                fromHeader);

            // Add required headers
            inviteRequest.Header.Contact = new List<SIPContactHeader>
            {
                new SIPContactHeader(null, new SIPURI(SIPSchemesEnum.sip,
                    SIPEndPoint.ParseSIPEndPoint($"{localIP}:{_localPort}")))
            };
            inviteRequest.Header.UserAgent = "RotaryPhoneController/1.0";
            inviteRequest.Header.ContentType = "application/sdp";

            // Create basic SDP for G.711 PCMU
            var localEndpoint = new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Parse(localIP), localRtpPort);
            var sdp = CreateBasicSDP(localEndpoint);
            inviteRequest.Body = sdp;
            inviteRequest.Header.ContentLength = sdp.Length;

            var targetEndpoint = new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Parse(targetIP), 5060);

            _logger.Information("INVITE target endpoint: {Endpoint}", targetEndpoint);
            _logger.Information("INVITE Content-Length: {Length}, Body length: {BodyLength}",
                inviteRequest.Header.ContentLength, inviteRequest.Body?.Length ?? 0);

            var sendResult = _sipTransport.SendRequestAsync(targetEndpoint, inviteRequest).Result;
            _pendingInviteRequest = inviteRequest;
            _inviteAnswered = false;

            if (sendResult != System.Net.Sockets.SocketError.Success)
            {
                _logger.Error("INVITE send failed with socket error: {Error}", sendResult);
                LogSipMessage(SipDirection.Sent, "INVITE", $"{localIP}:{_localPort}", targetEndpoint.ToString(),
                    callId: inviteRequest.Header.CallId, note: $"Send failed: {sendResult}");
            }
            else
            {
                _logger.Information("INVITE sent successfully to HT801");
                LogSipMessage(SipDirection.Sent, "INVITE", $"{localIP}:{_localPort}", targetEndpoint.ToString(),
                    callId: inviteRequest.Header.CallId);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send INVITE to HT801");
        }
    }

    public void CancelPendingInvite()
    {
        if (_pendingInviteRequest == null)
        {
            _logger.Debug("No pending INVITE to cancel");
            return;
        }

        var inviteReq = _pendingInviteRequest;
        var targetEndpoint = inviteReq.RemoteSIPEndPoint
            ?? new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Parse(inviteReq.URI.Host), 5060);

        try
        {
            if (_inviteAnswered)
            {
                // Established call — send BYE
                _logger.Information("Sending BYE to end call (Call-ID: {CallId})", inviteReq.Header.CallId);

                var byeRequest = SIPRequest.GetRequest(
                    SIPMethodsEnum.BYE,
                    inviteReq.URI,
                    inviteReq.Header.To,
                    inviteReq.Header.From);
                byeRequest.Header.CallId = inviteReq.Header.CallId;
                byeRequest.Header.CSeq = inviteReq.Header.CSeq + 1;
                byeRequest.Header.CSeqMethod = SIPMethodsEnum.BYE;
                byeRequest.Header.Contact = inviteReq.Header.Contact;
                byeRequest.Header.UserAgent = "RotaryPhoneController/1.0";

                _sipTransport?.SendRequestAsync(targetEndpoint, byeRequest);
                _logger.Information("BYE sent to HT801");
            }
            else
            {
                // Still ringing — send CANCEL with matching Via branch
                _logger.Information("Sending CANCEL for unanswered INVITE (Call-ID: {CallId})",
                    inviteReq.Header.CallId);

                var cancelReq = SIPRequest.GetRequest(
                    SIPMethodsEnum.CANCEL,
                    inviteReq.URI,
                    inviteReq.Header.To,
                    inviteReq.Header.From);
                cancelReq.Header.CallId = inviteReq.Header.CallId;
                cancelReq.Header.CSeq = inviteReq.Header.CSeq;
                cancelReq.Header.CSeqMethod = SIPMethodsEnum.CANCEL;
                // Via MUST match the original INVITE exactly for CANCEL to work
                cancelReq.Header.Vias = inviteReq.Header.Vias;
                cancelReq.Header.UserAgent = "RotaryPhoneController/1.0";

                _sipTransport?.SendRequestAsync(targetEndpoint, cancelReq);
                _logger.Information("CANCEL sent to HT801");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel/end call");
        }
        finally
        {
            _pendingInviteRequest = null;
            _inviteAnswered = false;
        }
    }

    /// <summary>
    /// Parse RTP port and IP from an SDP body (c= and m=audio lines).
    /// Returns (-1, "0.0.0.0") if parsing fails.
    /// </summary>
    internal static (int port, string ip) ExtractRtpDetailsFromSdp(string sdp)
    {
        int port = -1;
        string ip = "0.0.0.0";
        foreach (var line in sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("c=IN IP4 "))
                ip = line.Substring("c=IN IP4 ".Length).Trim();
            if (line.StartsWith("m=audio "))
            {
                var parts = line.Split(' ');
                if (parts.Length > 1 && int.TryParse(parts[1], out var p))
                    port = p;
            }
        }
        return (port > 0 ? port : -1, ip);
    }

    private string CreateBasicSDP(SIPEndPoint localEndpoint)
    {
        // Create a basic SDP for G.711 PCMU (codec 0)
        // RFC 4566 requires \r\n line endings in SDP
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sdp = $"v=0\r\n" +
                  $"o=RotaryPhone {sessionId} {sessionId} IN IP4 {localEndpoint.Address}\r\n" +
                  $"s=RotaryPhone Call\r\n" +
                  $"c=IN IP4 {localEndpoint.Address}\r\n" +
                  $"t=0 0\r\n" +
                  $"m=audio {localEndpoint.Port} RTP/AVP 0 101\r\n" +
                  $"a=rtpmap:0 PCMU/8000\r\n" +
                  $"a=rtpmap:101 telephone-event/8000\r\n" +
                  $"a=fmtp:101 0-15\r\n" +
                  $"a=sendrecv\r\n";
        return sdp;
    }

    // Public methods to trigger events for testing/simulation purposes
    public void TriggerHookChange(bool isOffHook)
    {
        _logger.Information("Triggering hook change event: {State}", isOffHook ? "OFF-HOOK" : "ON-HOOK");
        OnHookChange?.Invoke(isOffHook);
    }

    public void TriggerDigitsReceived(string number)
    {
        _logger.Information("Triggering digits received event: {Number}", number);
        OnDigitsReceived?.Invoke(number);
    }

    public void TriggerIncomingCall()
    {
        _logger.Information("Triggering incoming call event");
        OnIncomingCall?.Invoke();
    }

    /// <summary>
    /// Determine our local IP on the same subnet as the target device.
    /// </summary>
    private string GetLocalIPForTarget(string targetIP)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse(targetIP), 1);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not determine local IP for target {TargetIP}", targetIP);
        }
        return "192.168.86.50"; // fallback
    }

    public void Dispose()
    {
        _logger.Information("Disposing SIPSorceryAdapter");
        _userAgent?.Dispose();
        _sipTransport?.Shutdown();
    }
}
