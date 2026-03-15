using Xunit;
using Moq;
using RotaryPhoneController.Core;

namespace RotaryPhoneController.Tests;

public class CallAdapterRegistryTests
{
    private Mock<ICallAdapter> CreateMockAdapter(CallAdapterMode mode, bool available = true)
    {
        var mock = new Mock<ICallAdapter>();
        mock.Setup(a => a.Mode).Returns(mode);
        mock.Setup(a => a.IsAvailable).Returns(available);
        return mock;
    }

    [Fact]
    public void Register_AddsAdapter()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        registry.Register(bt.Object);
        Assert.Single(registry.AvailableModes);
        Assert.Contains(CallAdapterMode.BluetoothHfp, registry.AvailableModes);
    }

    [Fact]
    public async Task SwitchMode_ActivatesNewAdapter()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        var sip = CreateMockAdapter(CallAdapterMode.SipTrunk);
        registry.Register(bt.Object);
        registry.Register(sip.Object);
        await registry.SwitchModeAsync(CallAdapterMode.SipTrunk);
        Assert.Equal(CallAdapterMode.SipTrunk, registry.ActiveMode);
        sip.Verify(a => a.ActivateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwitchMode_DeactivatesPreviousAdapter()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        var sip = CreateMockAdapter(CallAdapterMode.SipTrunk);
        registry.Register(bt.Object);
        registry.Register(sip.Object);
        await registry.SwitchModeAsync(CallAdapterMode.BluetoothHfp);
        await registry.SwitchModeAsync(CallAdapterMode.SipTrunk);
        bt.Verify(a => a.DeactivateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwitchMode_FiresOnModeChanged()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        registry.Register(bt.Object);
        CallAdapterMode? firedMode = null;
        registry.OnModeChanged += mode => firedMode = mode;
        await registry.SwitchModeAsync(CallAdapterMode.BluetoothHfp);
        Assert.Equal(CallAdapterMode.BluetoothHfp, firedMode);
    }

    [Fact]
    public async Task SwitchMode_ThrowsForUnregisteredMode()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        registry.Register(bt.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SwitchModeAsync(CallAdapterMode.GVBrowser));
    }

    [Fact]
    public void ActiveAdapter_ThrowsWhenNoAdapterActive()
    {
        var registry = new CallAdapterRegistry();
        Assert.Throws<InvalidOperationException>(() => _ = registry.ActiveAdapter);
    }
}
