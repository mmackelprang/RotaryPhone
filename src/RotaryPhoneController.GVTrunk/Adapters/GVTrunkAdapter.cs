using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using Serilog;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace RotaryPhoneController.GVTrunk.Adapters;

public class GVTrunkAdapter : ITrunkAdapter, IDisposable
{
    private readonly TrunkConfig _config;
    private readonly ILogger _logger;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regAgent;
    private string? _activeCallId;

    public bool IsRegistered { get; private set; }
    public bool IsListening => _sipTransport != null;

    public event Action<bool>? OnHookChange;
    public event Action<string>? OnDigitsReceived;
    public event Action? OnIncomingCall;
    public event Action<bool>? OnRegistrationChanged;
    public event Action<string>? OnDtmfReceived;

    public GVTrunkAdapter(IOptions<TrunkConfig> config, ILogger logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public void StartListening()
    {
        if (_sipTransport != null) return;

        _sipTransport = new SIPTransport();

        var localIP = _config.LocalIp == "0.0.0.0"
            ? GetLocalIPForTarget(_config.SipServer)
            : _config.LocalIp;

        var listenEndpoint = new IPEndPoint(IPAddress.Parse(localIP), _config.LocalSipPort);
        _sipTransport.AddSIPChannel(new SIPUDPChannel(listenEndpoint));

        _sipTransport.SIPTransportRequestReceived += OnSIPRequestReceived;

        _logger.Information("GVTrunk SIP transport started on {IP}:{Port}", localIP, _config.LocalSipPort);
    }

    public async Task RegisterAsync(CancellationToken ct = default)
    {
        if (_sipTransport == null)
            StartListening();

        try
        {
            _regAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                _config.SipUsername,
                _config.SipPassword,
                _config.SipServer,
                _config.RegisterIntervalSeconds);

            _regAgent.RegistrationSuccessful += (uri, resp) =>
            {
                IsRegistered = true;
                OnRegistrationChanged?.Invoke(true);
                _logger.Information("GVTrunk registered with {Server}", _config.SipServer);
            };

            _regAgent.RegistrationFailed += (uri, resp, retry) =>
            {
                IsRegistered = false;
                OnRegistrationChanged?.Invoke(false);
                _logger.Warning("GVTrunk registration failed: {Response}", resp?.ReasonPhrase ?? "timeout");
            };

            _regAgent.RegistrationRemoved += (uri, resp) =>
            {
                IsRegistered = false;
                OnRegistrationChanged?.Invoke(false);
                _logger.Information("GVTrunk registration removed");
            };

            _regAgent.Start();
            _logger.Information("GVTrunk registration started for {User}@{Server}",
                _config.SipUsername, _config.SipServer);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GVTrunk registration error");
            IsRegistered = false;
            OnRegistrationChanged?.Invoke(false);
        }
    }

    public Task UnregisterAsync(CancellationToken ct = default)
    {
        _regAgent?.Stop();
        IsRegistered = false;
        OnRegistrationChanged?.Invoke(false);
        _logger.Information("GVTrunk unregistered");
        return Task.CompletedTask;
    }

    private Task<SocketError> OnSIPRequestReceived(SIPEndPoint localSIPEndPoint,
        SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        switch (sipRequest.Method)
        {
            case SIPMethodsEnum.INVITE:
                HandleInboundInvite(sipRequest, remoteEndPoint);
                break;
            case SIPMethodsEnum.BYE:
                HandleBye(sipRequest);
                break;
            case SIPMethodsEnum.CANCEL:
                HandleCancel(sipRequest);
                break;
            case SIPMethodsEnum.OPTIONS:
                var optResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                _sipTransport?.SendResponseAsync(optResp);
                break;
        }
        return Task.FromResult(SocketError.Success);
    }

    private void HandleInboundInvite(SIPRequest sipRequest, SIPEndPoint remoteEndPoint)
    {
        _logger.Information("GVTrunk inbound INVITE from {Remote}, caller: {From}",
            remoteEndPoint, sipRequest.Header.From?.FromURI?.User);

        var ringingResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ringing, null);
        _sipTransport?.SendResponseAsync(ringingResp);

        _activeCallId = sipRequest.Header.CallId;
        OnIncomingCall?.Invoke();
    }

    private void HandleBye(SIPRequest sipRequest)
    {
        _logger.Information("GVTrunk BYE received — call ended");
        var resp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        _sipTransport?.SendResponseAsync(resp);
        _activeCallId = null;
        OnHookChange?.Invoke(false);
    }

    private void HandleCancel(SIPRequest sipRequest)
    {
        _logger.Information("GVTrunk CANCEL received — caller hung up");
        var resp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        _sipTransport?.SendResponseAsync(resp);
        _activeCallId = null;
        OnHookChange?.Invoke(false);
    }

    public async Task<string> PlaceOutboundCallAsync(string e164Number)
    {
        if (!IsRegistered)
            throw new InvalidOperationException("Trunk not registered");
        if (_sipTransport == null)
            throw new InvalidOperationException("SIP transport not started");

        var localIP = _config.LocalIp == "0.0.0.0"
            ? GetLocalIPForTarget(_config.SipServer)
            : _config.LocalIp;

        var destUri = SIPURI.ParseSIPURIRelaxed($"sip:{e164Number}@{_config.SipServer}");
        var fromHeader = new SIPFromHeader(_config.OutboundCallerId,
            new SIPURI(_config.SipUsername, _config.SipServer, null),
            CallProperties.CreateNewTag());

        var inviteRequest = SIPRequest.GetRequest(
            SIPMethodsEnum.INVITE,
            destUri,
            new SIPToHeader(null, destUri, null),
            fromHeader);

        inviteRequest.Header.Contact = new List<SIPContactHeader>
        {
            new SIPContactHeader(null, new SIPURI(SIPSchemesEnum.sip,
                SIPEndPoint.ParseSIPEndPoint($"{localIP}:{_config.LocalSipPort}")))
        };
        inviteRequest.Header.UserAgent = "RotaryPhoneController-GVTrunk/1.0";

        var targetEndpoint = new SIPEndPoint(SIPProtocolsEnum.udp,
            IPAddress.Parse(await ResolveHostAsync(_config.SipServer)), _config.SipPort);

        var sendResult = await _sipTransport.SendRequestAsync(targetEndpoint, inviteRequest);
        _activeCallId = inviteRequest.Header.CallId;

        _logger.Information("GVTrunk outbound INVITE to {Number} via {Server}", e164Number, _config.SipServer);
        return _activeCallId;
    }

    public void SendInviteToHT801(string extensionToRing, string targetIP)
    {
        _logger.Debug("GVTrunk SendInviteToHT801 called — delegating to primary SIP adapter");
    }

    public void CancelPendingInvite()
    {
        _logger.Debug("GVTrunk CancelPendingInvite called");
        _activeCallId = null;
    }

    private string GetLocalIPForTarget(string targetHost)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var targetIP = Dns.GetHostAddresses(targetHost).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (targetIP != null)
            {
                socket.Connect(targetIP, 1);
                if (socket.LocalEndPoint is IPEndPoint ep)
                    return ep.Address.ToString();
            }
        }
        catch { }
        return "0.0.0.0";
    }

    private async Task<string> ResolveHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        var addresses = await Dns.GetHostAddressesAsync(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()
            ?? throw new Exception($"Cannot resolve {host}");
    }

    public void Dispose()
    {
        _regAgent?.Stop();
        _sipTransport?.Shutdown();
    }
}
