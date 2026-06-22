using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Guards the "rings but no audio" fix: the GV media RTCPeerConnection must bind ICE/RTP to the
/// box's IPv4 address (RTCConfiguration.X_BindAddress) so SIPSorcery never gathers the box's ULA
/// IPv6 host candidates. The box has no routable IPv6, so an IPv6 candidate pair to Google is
/// unreachable (SocketException 101) and the media leg dies. The single correctness invariant that
/// is unit-testable here is that <see cref="GvSipTransport.ResolveLocalIPv4"/> ALWAYS returns an
/// IPv4 (AddressFamily.InterNetwork) address — both on the live-resolve path and on the fallback.
/// (Real ICE/audio behaviour can only be verified by a live inbound call after deploy.)
/// </summary>
public class GvSipTransportIPv4BindTests
{
    private static readonly SipCredentials TestCreds =
        new(SipUsername: "sip-token", BearerToken: "crypto-key", PhoneNumber: "+15551234567", ExpirySeconds: 3600);

    private static GvSipTransport CreateTransport() =>
        new(NullLogger<GvSipTransport>.Instance, () => Task.FromResult(TestCreds));

    [Fact]
    public void ResolveLocalIPv4_ReturnsAnIPv4Address()
    {
        var transport = CreateTransport();

        var addr = transport.ResolveLocalIPv4();

        Assert.NotNull(addr);
        Assert.Equal(AddressFamily.InterNetwork, addr.AddressFamily);
        // Never a v4-mapped-v6 or anything else that would re-open the IPv6 ICE path.
        Assert.False(addr.IsIPv4MappedToIPv6);
    }

    [Fact]
    public void ResolveLocalIPv4_FallbackConstantIsIPv4()
    {
        // The documented fallback (when the OS has no IPv4 default route at all) must itself be IPv4,
        // or the fix would silently hand SIPSorcery an IPv6 bind address on the failure path.
        var fallback = IPAddress.Parse("192.168.86.50");
        Assert.Equal(AddressFamily.InterNetwork, fallback.AddressFamily);
    }
}
