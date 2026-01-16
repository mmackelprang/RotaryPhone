using System.Net;
using System.Net.Sockets;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core;

public class SIPSorceryAdapter : ISipAdapter
{
    private readonly ILogger _logger;
    private SIPTransport? _sipTransport;
    private SIPUserAgent? _userAgent;
    private readonly string _localIPAddress;
    private readonly int _localPort;

    public event Action<bool>? OnHookChange;
    public event Action<string>? OnDigitsReceived;
    public event Action? OnIncomingCall;

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
                    break;
                case SIPMethodsEnum.INFO:
                    HandleInfo(sipRequest);
                    break;
                case SIPMethodsEnum.INVITE:
                    HandleInvite(sipRequest);
                    break;
                case SIPMethodsEnum.BYE:
                    HandleBye(sipRequest);
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
        
        try
        {
            var dialedNumber = sipRequest.Header.To.ToURI.User;
            if (!string.IsNullOrEmpty(dialedNumber))
            {
                _logger.Information("User dialed: {Number}", dialedNumber);
                
                // Trigger digits received with the full number
                OnDigitsReceived?.Invoke(dialedNumber);
                
                // Important: We must answer the INVITE to establish the SIP dialog
                // and stop the HT801 from retransmitting.
                var response = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                
                // Add Contact header to response (required)
                response.Header.Contact = new List<SIPContactHeader> 
                { 
                    new SIPContactHeader(null, new SIPURI(SIPSchemesEnum.sip, 
                        SIPEndPoint.ParseSIPEndPoint($"{_localIPAddress}:{_localPort}"))) 
                };
                
                // Add SDP to response (negotiate codec)
                // Use the same RTP port we configured for the bridge
                var localEndpoint = SIPEndPoint.ParseSIPEndPoint($"{_localIPAddress}:49000");
                response.Body = CreateBasicSDP(localEndpoint);
                
                _sipTransport?.SendResponseAsync(response);
                
                // Also trigger hook change to ensure we are in InCall state
                OnHookChange?.Invoke(true);
            }
            else
            {
                _logger.Warning("Received INVITE without a user in To header");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing INVITE");
        }
    }

    private void HandleBye(SIPRequest sipRequest)
    {
        _logger.Information("Processing BYE message - Call terminated by remote party");
        // Fire OnHookChange with false (on-hook) to simulate hanging up
        OnHookChange?.Invoke(false);
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

    public void SendInviteToHT801(string extensionToRing, string targetIP)
    {
        try
        {
            _logger.Information("Sending INVITE to HT801 at {IP} for extension {Extension}", targetIP, extensionToRing);

            if (_sipTransport == null)
            {
                _logger.Error("SIP transport is not initialized. Call StartListening() first.");
                return;
            }

            // Create destination URI
            var destinationUri = SIPURI.ParseSIPURIRelaxed($"sip:{extensionToRing}@{targetIP}");
            
            // Create local SIP endpoint
            var fromHeader = new SIPFromHeader(null, 
                new SIPURI(extensionToRing, _localIPAddress, null, SIPSchemesEnum.sip, SIPProtocolsEnum.udp), 
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
                    SIPEndPoint.ParseSIPEndPoint($"{_localIPAddress}:{_localPort}"))) 
            };
            inviteRequest.Header.UserAgent = "RotaryPhoneController/1.0";
            inviteRequest.Header.ContentType = "application/sdp";

            // Create basic SDP for G.711 PCMU
            var localEndpoint = SIPEndPoint.ParseSIPEndPoint($"{_localIPAddress}:49000");
            var sdp = CreateBasicSDP(localEndpoint);
            inviteRequest.Body = sdp;

            _logger.Debug("INVITE request: {Request}", inviteRequest.ToString());
            _logger.Debug("SDP body: {SDP}", sdp);

            // Send the INVITE
            var targetEndpoint = SIPEndPoint.ParseSIPEndPoint($"{targetIP}:5060");
            _sipTransport.SendRequestAsync(targetEndpoint, inviteRequest);

            _logger.Information("INVITE sent successfully to HT801");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send INVITE to HT801");
        }
    }

    private string CreateBasicSDP(SIPEndPoint localEndpoint)
    {
        // Create a basic SDP for G.711 PCMU (codec 0)
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sdp = $@"v=0
o=RotaryPhone {sessionId} {sessionId} IN IP4 {localEndpoint.Address}
s=RotaryPhone Call
c=IN IP4 {localEndpoint.Address}
t=0 0
m=audio {localEndpoint.Port} RTP/AVP 0 101
a=rtpmap:0 PCMU/8000
a=rtpmap:101 telephone-event/8000
a=fmtp:101 0-15
a=sendrecv
";
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

    public void Dispose()
    {
        _logger.Information("Disposing SIPSorceryAdapter");
        _userAgent?.Dispose();
        _sipTransport?.Shutdown();
    }
}
