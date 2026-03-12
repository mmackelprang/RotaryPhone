using RotaryPhoneController.Core.Audio;

namespace RotaryPhoneController.Tests;

public class BluetoothDisconnectReasonTests
{
  [Theory]
  [InlineData(BluetoothDisconnectReason.Unknown, false)]
  [InlineData(BluetoothDisconnectReason.Timeout, false)]
  [InlineData(BluetoothDisconnectReason.LocalHost, true)]
  [InlineData(BluetoothDisconnectReason.Remote, true)]
  [InlineData(BluetoothDisconnectReason.AuthFailure, true)]
  [InlineData(BluetoothDisconnectReason.LocalHostSuspend, true)]
  public void ShouldSuppressReconnect_ReturnsCorrectValue(BluetoothDisconnectReason reason, bool expected)
  {
    Assert.Equal(expected, reason.ShouldSuppressReconnect());
  }

  [Fact]
  public void Enum_HasExpectedValues()
  {
    Assert.Equal(0x00, (byte)BluetoothDisconnectReason.Unknown);
    Assert.Equal(0x01, (byte)BluetoothDisconnectReason.Timeout);
    Assert.Equal(0x02, (byte)BluetoothDisconnectReason.LocalHost);
    Assert.Equal(0x03, (byte)BluetoothDisconnectReason.Remote);
    Assert.Equal(0x04, (byte)BluetoothDisconnectReason.AuthFailure);
    Assert.Equal(0x05, (byte)BluetoothDisconnectReason.LocalHostSuspend);
  }
}
