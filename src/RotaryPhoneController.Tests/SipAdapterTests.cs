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
}
