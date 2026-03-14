using Moq;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Tests;

public class BlueZBtManagerTests
{
    [Fact]
    public void ProcessEvent_Ring_FiresOnIncomingCall()
    {
        var manager = CreateManager();
        string? receivedNumber = null;
        BluetoothDevice? receivedDevice = null;
        manager.OnIncomingCall += (dev, num) => { receivedDevice = dev; receivedNumber = num; };

        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"+15551234567"}""");

        Assert.NotNull(receivedDevice);
        Assert.Equal("D4:3A:2C:64:87:9E", receivedDevice!.Address);
        Assert.Equal("+15551234567", receivedNumber);
        Assert.True(receivedDevice.HasIncomingCall);
    }

    [Fact]
    public void ProcessEvent_CallActive_WithoutATA_FiresCallAnsweredOnPhone()
    {
        var manager = CreateManager();
        BluetoothDevice? answeredDevice = null;
        manager.OnCallAnsweredOnPhone += dev => answeredDevice = dev;

        // Simulate ring then call_active without us sending ATA
        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"Unknown"}""");
        manager.ProcessEventForTest("""{"event":"call_active","address":"D4:3A:2C:64:87:9E","answered_locally":true}""");

        Assert.NotNull(answeredDevice);
        Assert.Equal("D4:3A:2C:64:87:9E", answeredDevice!.Address);
    }

    [Fact]
    public void ProcessEvent_CallActive_AfterATA_DoesNotFireCallAnsweredOnPhone()
    {
        var manager = CreateManager();
        bool answeredOnPhone = false;
        manager.OnCallAnsweredOnPhone += _ => answeredOnPhone = true;

        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"Unknown"}""");
        // Simulate that we sent ATA (by calling AnswerCallAsync which sets the flag)
        manager.MarkAnswerSent("D4:3A:2C:64:87:9E");
        manager.ProcessEventForTest("""{"event":"call_active","address":"D4:3A:2C:64:87:9E"}""");

        Assert.False(answeredOnPhone);
    }

    [Fact]
    public void ProcessEvent_CallEnded_FiresOnCallEnded()
    {
        var manager = CreateManager();
        BluetoothDevice? endedDevice = null;
        manager.OnCallEnded += dev => endedDevice = dev;

        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"x"}""");
        manager.ProcessEventForTest("""{"event":"call_ended","address":"D4:3A:2C:64:87:9E"}""");

        Assert.NotNull(endedDevice);
    }

    [Fact]
    public void ProcessEvent_Connected_TracksDevice()
    {
        var manager = CreateManager();

        manager.ProcessEventForTest("""{"event":"connected","address":"D4:3A:2C:64:87:9E","name":"Pixel 8 Pro"}""");

        Assert.Single(manager.ConnectedDevices);
        Assert.Equal("Pixel 8 Pro", manager.ConnectedDevices[0].Name);
    }

    [Fact]
    public void ProcessEvent_Disconnected_RemovesFromConnected()
    {
        var manager = CreateManager();
        manager.ProcessEventForTest("""{"event":"connected","address":"D4:3A:2C:64:87:9E","name":"Pixel"}""");
        manager.ProcessEventForTest("""{"event":"disconnected","address":"D4:3A:2C:64:87:9E"}""");

        Assert.Empty(manager.ConnectedDevices);
    }

    [Fact]
    public void ProcessEvent_ScoConnected_FiresEvent()
    {
        var manager = CreateManager();
        BluetoothDevice? scoDevice = null;
        manager.OnScoAudioConnected += dev => scoDevice = dev;

        manager.ProcessEventForTest("""{"event":"connected","address":"D4:3A:2C:64:87:9E","name":"Pixel"}""");
        manager.ProcessEventForTest("""{"event":"sco_connected","address":"D4:3A:2C:64:87:9E","codec":"CVSD"}""");

        Assert.NotNull(scoDevice);
        Assert.True(scoDevice!.HasScoAudio);
    }

    private static BlueZBtManager CreateManager()
    {
        var logger = new Mock<ILogger<BlueZBtManager>>();
        var config = new AppConfiguration { BluetoothAdapter = "hci1" };
        return new BlueZBtManager(logger.Object, config);
    }
}
