using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core;

namespace RotaryPhoneController.Tests;

public class SipAdapterTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly SIPSorceryAdapter _adapter;

    public SipAdapterTests()
    {
        _mockLogger = new Mock<Serilog.ILogger>();
        _adapter = new SIPSorceryAdapter(_mockLogger.Object, "127.0.0.1", 5060);
    }

    [Fact]
    public void TriggerHookChange_ShouldFireEvent()
    {
        bool? result = null;
        _adapter.OnHookChange += (state) => result = state;

        _adapter.TriggerHookChange(true);

        Assert.True(result);
    }

    [Fact]
    public void TriggerDigitsReceived_ShouldFireEvent()
    {
        string? result = null;
        _adapter.OnDigitsReceived += (digits) => result = digits;

        _adapter.TriggerDigitsReceived("123");

        Assert.Equal("123", result);
    }

    [Fact]
    public void TriggerIncomingCall_ShouldFireEvent()
    {
        bool fired = false;
        _adapter.OnIncomingCall += () => fired = true;

        _adapter.TriggerIncomingCall();

        Assert.True(fired);
    }

    [Theory]
    [InlineData("9193718044", true)]
    [InlineData("+19193718044", true)]
    [InlineData("911", true)]
    [InlineData("*67", true)]
    [InlineData("#123", true)]
    [InlineData("+1*555#1234", true)]
    [InlineData("rotaryphone", false)]
    [InlineData("admin", false)]
    [InlineData("sip-user", false)]
    [InlineData("user123name", false)]
    [InlineData("", false)]
    public void IsDialableNumber_ClassifiesCorrectly(string input, bool expected)
    {
        Assert.Equal(expected, SIPSorceryAdapter.IsDialableNumber(input));
    }

    [Fact]
    public void IsDialableNumber_NullInput_ReturnsFalse()
    {
        Assert.False(SIPSorceryAdapter.IsDialableNumber(null!));
    }

    [Fact]
    public void ExtractRtpDetailsFromSdp_TypicalHT801Sdp_ReturnsPortAndIp()
    {
        // Typical SDP body from an HT801 INVITE
        var sdp = "v=0\r\n" +
                  "o=- 12345 12345 IN IP4 192.168.86.22\r\n" +
                  "s=GrandStream\r\n" +
                  "c=IN IP4 192.168.86.22\r\n" +
                  "t=0 0\r\n" +
                  "m=audio 5004 RTP/AVP 0 101\r\n" +
                  "a=rtpmap:0 PCMU/8000\r\n" +
                  "a=rtpmap:101 telephone-event/8000\r\n" +
                  "a=fmtp:101 0-15\r\n" +
                  "a=sendrecv\r\n";

        var (port, ip) = SIPSorceryAdapter.ExtractRtpDetailsFromSdp(sdp);

        Assert.Equal(5004, port);
        Assert.Equal("192.168.86.22", ip);
    }

    [Fact]
    public void ExtractRtpDetailsFromSdp_DifferentPort_ReturnsCorrectPort()
    {
        // HT801 may use a non-default RTP port
        var sdp = "v=0\r\n" +
                  "c=IN IP4 10.0.0.5\r\n" +
                  "m=audio 16384 RTP/AVP 0\r\n";

        var (port, ip) = SIPSorceryAdapter.ExtractRtpDetailsFromSdp(sdp);

        Assert.Equal(16384, port);
        Assert.Equal("10.0.0.5", ip);
    }

    [Fact]
    public void ExtractRtpDetailsFromSdp_NoSdpBody_ReturnsDefaults()
    {
        var (port, ip) = SIPSorceryAdapter.ExtractRtpDetailsFromSdp("");

        Assert.Equal(-1, port);
        Assert.Equal("0.0.0.0", ip);
    }

    [Fact]
    public void ExtractRtpDetailsFromSdp_MissingMediaLine_ReturnsNegativePort()
    {
        var sdp = "v=0\r\nc=IN IP4 192.168.86.22\r\n";

        var (port, ip) = SIPSorceryAdapter.ExtractRtpDetailsFromSdp(sdp);

        Assert.Equal(-1, port);
        Assert.Equal("192.168.86.22", ip);
    }

    [Fact]
    public void ExtractRtpDetailsFromSdp_MissingConnectionLine_ReturnsDefaultIp()
    {
        var sdp = "v=0\r\nm=audio 5004 RTP/AVP 0\r\n";

        var (port, ip) = SIPSorceryAdapter.ExtractRtpDetailsFromSdp(sdp);

        Assert.Equal(5004, port);
        Assert.Equal("0.0.0.0", ip);
    }

    [Fact]
    public void ExtractRtpDetailsFromSdp_UnixLineEndings_ParsesCorrectly()
    {
        // SDP with Unix-style \n line endings
        var sdp = "v=0\nc=IN IP4 192.168.86.22\nm=audio 5004 RTP/AVP 0 101\na=sendrecv\n";

        var (port, ip) = SIPSorceryAdapter.ExtractRtpDetailsFromSdp(sdp);

        Assert.Equal(5004, port);
        Assert.Equal("192.168.86.22", ip);
    }

    // ---------------------------------------------------------------------
    // 200 OK answer to an HT801 outbound-call INVITE (the UAS path).
    //
    // Regression coverage for the malformed 200 OK that left outbound calls
    // with 0 RTP in both directions: the response carried an SDP body and a
    // Content-Length but no Content-Type, so the HT801 silently discarded the
    // SDP and never learned RotaryPhone's RTP port (49000). SIPSorcery's Body
    // setter does NOT set Content-Type; it must be set explicitly. A
    // dialog-establishing 2xx also MUST carry a To-tag (RFC 3261 §12.1.1).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds a representative HT801 outbound-call INVITE: a To header with no
    /// tag (as a UAC's initial INVITE has) and a G.711 SDP offer.
    /// </summary>
    private static SIPSorcery.SIP.SIPRequest BuildHT801OutboundInvite()
    {
        var targetUri = SIPSorcery.SIP.SIPURI.ParseSIPURI("sip:9193718044@192.168.86.50:5060");
        var fromHeader = new SIPSorcery.SIP.SIPFromHeader(
            "rotaryphone",
            SIPSorcery.SIP.SIPURI.ParseSIPURI("sip:rotaryphone@192.168.86.250:5060"),
            SIPSorcery.SIP.CallProperties.CreateNewTag());
        // UAC's initial INVITE: To header carries NO tag.
        var toHeader = new SIPSorcery.SIP.SIPToHeader(null, targetUri, null);

        var invite = SIPSorcery.SIP.SIPRequest.GetRequest(
            SIPSorcery.SIP.SIPMethodsEnum.INVITE, targetUri, toHeader, fromHeader);

        invite.Header.ContentType = "application/sdp";
        invite.Body =
            "v=0\r\n" +
            "o=- 12345 12345 IN IP4 192.168.86.250\r\n" +
            "s=GrandStream\r\n" +
            "c=IN IP4 192.168.86.250\r\n" +
            "t=0 0\r\n" +
            "m=audio 5004 RTP/AVP 0 101\r\n" +
            "a=rtpmap:0 PCMU/8000\r\n" +
            "a=sendrecv\r\n";
        return invite;
    }

    [Fact]
    public void BuildInviteOkResponse_SetsContentTypeApplicationSdp()
    {
        var invite = BuildHT801OutboundInvite();

        var response = SIPSorceryAdapter.BuildInviteOkResponse(invite, "192.168.86.50", 5060, 49000);

        // The whole point of the fix: a 200 OK with an SDP body MUST declare
        // Content-Type: application/sdp or the HT801 ignores the SDP.
        Assert.Equal("application/sdp", response.Header.ContentType);
    }

    [Fact]
    public void BuildInviteOkResponse_CarriesSdpBodyWithBridgeRtpPort()
    {
        var invite = BuildHT801OutboundInvite();

        var response = SIPSorceryAdapter.BuildInviteOkResponse(invite, "192.168.86.50", 5060, 49000);

        Assert.False(string.IsNullOrEmpty(response.Body));
        // SDP must advertise the bridge bind port (49000) and our local IP so the
        // HT801 sends its RTP to the right destination.
        Assert.Contains("m=audio 49000 RTP/AVP", response.Body);
        Assert.Contains("c=IN IP4 192.168.86.50", response.Body);
    }

    [Fact]
    public void BuildInviteOkResponse_AddsNonEmptyToTag()
    {
        var invite = BuildHT801OutboundInvite();
        Assert.True(string.IsNullOrEmpty(invite.Header.To.ToTag)); // precondition: UAC INVITE has no To-tag

        var response = SIPSorceryAdapter.BuildInviteOkResponse(invite, "192.168.86.50", 5060, 49000);

        // A UAS MUST add a To-tag to a dialog-establishing 2xx (RFC 3261 §12.1.1).
        Assert.False(string.IsNullOrEmpty(response.Header.To.ToTag));
    }

    [Fact]
    public void BuildInviteOkResponse_Returns200OkWithContactHeader()
    {
        var invite = BuildHT801OutboundInvite();

        var response = SIPSorceryAdapter.BuildInviteOkResponse(invite, "192.168.86.50", 5060, 49000);

        Assert.Equal(SIPSorcery.SIP.SIPResponseStatusCodesEnum.Ok, response.Status);
        Assert.NotNull(response.Header.Contact);
        Assert.NotEmpty(response.Header.Contact);
    }
}
