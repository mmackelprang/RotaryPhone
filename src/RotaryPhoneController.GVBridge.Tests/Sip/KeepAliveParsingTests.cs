using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Pure-function tests for parsing the RFC 6223 <c>keep=</c> recommended keep-alive
/// frequency from a REGISTER 200-OK first Via header.
/// </summary>
public class KeepAliveParsingTests
{
    private const int Default = 120;

    [Theory]
    [InlineData("Via: SIP/2.0/WSS host.invalid;branch=z9;keep=240\r\n", 240)]
    [InlineData("Via: SIP/2.0/WSS host.invalid;branch=z9;keep=180\r\n", 180)]
    [InlineData("Via: SIP/2.0/WSS host.invalid;branch=z9;keep=30\r\n", 30)]
    public void ParsesNumericKeep(string via, int expected)
    {
        var message = via + "Content-Length: 0\r\n\r\n";
        Assert.Equal(expected, GvSipTransport.ParseKeepInterval(message, Default));
    }

    [Fact]
    public void FlagOnlyKeep_ReturnsDefault()
    {
        // Bare ";keep" flag with no value is the client's request indication, not a frequency.
        var message = "Via: SIP/2.0/WSS host.invalid;branch=z9;keep\r\nContent-Length: 0\r\n\r\n";
        Assert.Equal(Default, GvSipTransport.ParseKeepInterval(message, Default));
    }

    [Fact]
    public void MissingKeep_ReturnsDefault()
    {
        var message = "Via: SIP/2.0/WSS host.invalid;branch=z9\r\nContent-Length: 0\r\n\r\n";
        Assert.Equal(Default, GvSipTransport.ParseKeepInterval(message, Default));
    }

    [Fact]
    public void MalformedKeep_ReturnsDefault()
    {
        var message = "Via: SIP/2.0/WSS host.invalid;branch=z9;keep=abc\r\nContent-Length: 0\r\n\r\n";
        Assert.Equal(Default, GvSipTransport.ParseKeepInterval(message, Default));
    }

    [Fact]
    public void NoViaHeader_ReturnsDefault()
    {
        var message = "Content-Length: 0\r\n\r\n";
        Assert.Equal(Default, GvSipTransport.ParseKeepInterval(message, Default));
    }

    [Fact]
    public void EmptyMessage_ReturnsDefault()
    {
        Assert.Equal(Default, GvSipTransport.ParseKeepInterval("", Default));
    }

    [Fact]
    public void UsesFirstVia_WhenMultiplePresent()
    {
        // Only the FIRST (topmost) Via's keep= is meaningful.
        var message =
            "Via: SIP/2.0/WSS host.invalid;branch=z9;keep=240\r\n" +
            "Via: SIP/2.0/WSS other.invalid;branch=z8;keep=60\r\n" +
            "Content-Length: 0\r\n\r\n";
        Assert.Equal(240, GvSipTransport.ParseKeepInterval(message, Default));
    }
}
