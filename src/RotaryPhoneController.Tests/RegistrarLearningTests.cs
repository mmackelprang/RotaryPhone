using System.Net;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Sip;
using Serilog;
using SIPSorcery.SIP;

namespace RotaryPhoneController.Tests;

/// <summary>
/// Pins <see cref="SIPSorceryAdapter.HandleRegister"/> — the binding-learning rules of plan decision D3.
///
/// The single most load-bearing rule is that the binding takes the REGISTER's SOURCE address and NOT the
/// Contact host: a misconfigured ATA can advertise a stale host in Contact, whereas the source address of
/// the REGISTER just provably delivered a datagram.
///
/// No live transport is needed: _sipTransport is null unless StartListening() was called, so the
/// SendResponseAsync call inside HandleRegister is a null-conditional no-op.
/// </summary>
public class RegistrarLearningTests
{
    private const string SourceIp = "192.0.2.240";     // where the REGISTER actually came from
    private const string ContactIp = "192.0.2.22";     // stale host the device advertises in Contact
    private const int SourcePort = 5062;

    private static SIPEndPoint EndPoint(string ip, int port) =>
        new(SIPProtocolsEnum.udp, IPAddress.Parse(ip), port);

    /// <summary>
    /// Builds a REGISTER whose Contact deliberately advertises a DIFFERENT host from the source address,
    /// so tests can tell which one the binding actually used.
    /// </summary>
    private static SIPRequest Register(
        string aor = "rotaryphone",
        string contactHost = ContactIp,
        long? expires = 3600,
        string fromUser = "someone-else")
    {
        var registrarUri = SIPURI.ParseSIPURI("sip:192.0.2.1");
        var to = new SIPToHeader(null, SIPURI.ParseSIPURI($"sip:{aor}@192.0.2.1"), null);
        var from = new SIPFromHeader(null, SIPURI.ParseSIPURI($"sip:{fromUser}@192.0.2.1"),
            CallProperties.CreateNewTag());

        var request = SIPRequest.GetRequest(SIPMethodsEnum.REGISTER, registrarUri, to, from);
        request.Header.Contact =
        [
            new SIPContactHeader(null, SIPURI.ParseSIPURI($"sip:{aor}@{contactHost}"))
        ];

        // SIPSorcery's default when the header is absent is -1; leave it alone to model "no Expires".
        if (expires.HasValue) request.Header.Expires = expires.Value;

        return request;
    }

    private static (SIPSorceryAdapter Adapter, RegistrarBindingStore Store) NewAdapter()
    {
        var store = new RegistrarBindingStore();
        return (new SIPSorceryAdapter(Mock.Of<ILogger>(), "0.0.0.0", 5060, store), store);
    }

    private static void Handle(SIPSorceryAdapter adapter, SIPRequest request) =>
        adapter.HandleRegister(request, EndPoint("192.0.2.1", 5060), EndPoint(SourceIp, SourcePort));

    [Fact]
    public void Learns_TheSourceAddress_NotTheContactHost()
    {
        var (adapter, store) = NewAdapter();

        Handle(adapter, Register());

        var binding = store.Get("rotaryphone");
        Assert.NotNull(binding);

        // THE rule of D3: INVITEs go to where the REGISTER came from.
        Assert.Equal(SourceIp, binding.Address);
        Assert.Equal(SourcePort, binding.Port);

        // Contact is retained for diagnostics only — and must NOT be what we ring.
        Assert.Equal(ContactIp, binding.ContactHost);
        Assert.NotEqual(binding.ContactHost, binding.Address);
    }

    [Fact]
    public void AddressOfRecord_ComesFromTheToHeaderUserPart()
    {
        var (adapter, store) = NewAdapter();

        // From deliberately carries a different user, so a From-based implementation would fail here.
        Handle(adapter, Register(aor: "1000", fromUser: "not-the-aor"));

        Assert.Equal(SourceIp, store.Get("1000")?.Address);
        Assert.Null(store.Get("not-the-aor"));
    }

    [Fact]
    public void ExpiresZero_RemovesAnExistingBinding()
    {
        var (adapter, store) = NewAdapter();

        Handle(adapter, Register());
        Assert.NotNull(store.Get("rotaryphone"));

        // Explicit de-registration: the device is telling us it is going away.
        Handle(adapter, Register(expires: 0));

        Assert.Null(store.Get("rotaryphone"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void MissingExpiresHeader_RecordsA3600SecondBinding_AndDoesNotRemove()
    {
        var (adapter, store) = NewAdapter();

        // SIPSorcery reports -1 when no Expires header is present. That must NOT be read as a
        // de-registration, and must not wrap into a negative (instantly stale) expiry.
        var request = Register(expires: null);
        Assert.True(request.Header.Expires < 0, "precondition: absent Expires is modelled as negative");

        Handle(adapter, request);

        var binding = store.Get("rotaryphone");
        Assert.NotNull(binding);
        Assert.Equal(3600, binding.ExpiresSeconds);
        Assert.True(binding.IsFresh(DateTime.UtcNow));
    }

    [Fact]
    public void ReRegisteringFromANewAddress_MovesTheBinding()
    {
        var (adapter, store) = NewAdapter();

        Handle(adapter, Register());
        Assert.Equal(SourceIp, store.Get("rotaryphone")?.Address);

        // The DHCP-move case this whole mechanism exists for.
        const string movedIp = "192.0.2.241";
        adapter.HandleRegister(Register(), EndPoint("192.0.2.1", 5060), EndPoint(movedIp, SourcePort));

        Assert.Single(store.All());
        Assert.Equal(movedIp, store.Get("rotaryphone")?.Address);
    }
}
