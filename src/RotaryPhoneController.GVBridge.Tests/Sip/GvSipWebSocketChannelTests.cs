using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// Cheap-to-test bits of the WebSocket channel that don't require a live socket.
/// Deep receive-loop behavior is covered indirectly via the fake-channel transport tests.
/// </summary>
public class GvSipWebSocketChannelTests
{
    [Fact]
    public void IsConnected_IsFalse_BeforeConnect()
    {
        var channel = new GvSipWebSocketChannel(
            new Uri("wss://example.invalid/websocket"),
            NullLogger.Instance);

        Assert.False(channel.IsConnected);
    }

    [Fact]
    public void WebSocketClosedEventArgs_CarriesWasIntentional()
    {
        var intentional = new WebSocketClosedEventArgs(wasIntentional: true, description: "closing");
        var unexpected = new WebSocketClosedEventArgs(wasIntentional: false, description: "remote closed");

        Assert.True(intentional.WasIntentional);
        Assert.Equal("closing", intentional.Description);
        Assert.False(unexpected.WasIntentional);
        Assert.Equal("remote closed", unexpected.Description);
    }

    [Fact]
    public void Closed_Event_RaisesWithExpectedArgs()
    {
        var channel = new GvSipWebSocketChannel(
            new Uri("wss://example.invalid/websocket"),
            NullLogger.Instance);

        WebSocketClosedEventArgs? received = null;
        channel.Closed += (_, e) => received = e;

        // Manually raise to verify the event surface compiles and carries the flag.
        channel.RaiseClosedForTest(wasIntentional: false, description: "test");

        Assert.NotNull(received);
        Assert.False(received!.WasIntentional);
        Assert.Equal("test", received.Description);
    }

    [Fact]
    public void Channel_ImplementsInterface()
    {
        ISipWebSocketChannel channel = new GvSipWebSocketChannel(
            new Uri("wss://example.invalid/websocket"),
            NullLogger.Instance);

        Assert.False(channel.IsConnected);
    }
}
