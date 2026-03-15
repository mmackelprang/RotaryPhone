using Xunit;
using Moq;
using RotaryPhoneController.GVTrunk.Adapters;
using RotaryPhoneController.GVTrunk.Models;
using Microsoft.Extensions.Options;
using Serilog;

namespace RotaryPhoneController.GVTrunk.Tests;

public class GVTrunkAdapterTests
{
    private readonly TrunkConfig _config = new()
    {
        SipServer = "sip.voip.ms",
        SipPort = 5060,
        SipUsername = "testuser",
        SipPassword = "testpass",
        LocalSipPort = 15061,
        LocalIp = "127.0.0.1",
        OutboundCallerId = "+15551234567"
    };

    private GVTrunkAdapter CreateAdapter()
    {
        var options = Options.Create(_config);
        var logger = new Mock<ILogger>().Object;
        return new GVTrunkAdapter(options, logger);
    }

    [Fact]
    public void InitialState_IsNotRegistered()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsRegistered);
    }

    [Fact]
    public void InitialState_IsNotListening()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsListening);
    }

    [Fact]
    public void StartListening_SetsIsListeningTrue()
    {
        var adapter = CreateAdapter();
        adapter.StartListening();
        Assert.True(adapter.IsListening);
        adapter.Dispose();
    }

    [Fact]
    public void OnIncomingCall_EventCanBeSubscribed()
    {
        var adapter = CreateAdapter();
        bool fired = false;
        adapter.OnIncomingCall += () => fired = true;
        Assert.False(fired);
    }

    [Fact]
    public void OnRegistrationChanged_EventCanBeSubscribed()
    {
        var adapter = CreateAdapter();
        bool? lastValue = null;
        adapter.OnRegistrationChanged += (registered) => lastValue = registered;
        Assert.Null(lastValue);
    }
}
