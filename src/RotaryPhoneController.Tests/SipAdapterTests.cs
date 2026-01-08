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
}
