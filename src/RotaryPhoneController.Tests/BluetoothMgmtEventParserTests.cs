#if !WINDOWS
using RotaryPhoneController.Core.Audio;

namespace RotaryPhoneController.Tests;

public class BluetoothMgmtEventParserTests
{
  // MGMT_EV_DEVICE_DISCONNECTED opcode
  private const ushort EvDisconnected = 0x000C;

  /// <summary>
  /// Build a valid MGMT_EV_DEVICE_DISCONNECTED packet.
  /// Layout: opcode(2) + index(2) + param_len(2) + bdaddr(6) + addr_type(1) + reason(1)
  /// </summary>
  private static byte[] BuildDisconnectedEvent(byte[] bdAddr, byte addrType, BluetoothDisconnectReason reason, ushort index = 0)
  {
    var packet = new byte[14]; // 6 header + 8 payload
    // opcode (LE)
    packet[0] = (byte)(EvDisconnected & 0xFF);
    packet[1] = (byte)(EvDisconnected >> 8);
    // index (LE)
    packet[2] = (byte)(index & 0xFF);
    packet[3] = (byte)(index >> 8);
    // param_len (LE) = 8
    packet[4] = 8;
    packet[5] = 0;
    // bdaddr (6 bytes, little-endian in kernel)
    Array.Copy(bdAddr, 0, packet, 6, 6);
    // addr_type
    packet[12] = addrType;
    // reason
    packet[13] = (byte)reason;
    return packet;
  }

  [Fact]
  public void TryParseDeviceDisconnected_ValidRemoteDisconnect_ReturnsTrue()
  {
    // BD_ADDR D4:3A:2C:64:87:9E stored in little-endian
    var bdAddr = new byte[] { 0x9E, 0x87, 0x64, 0x2C, 0x3A, 0xD4 };
    var packet = BuildDisconnectedEvent(bdAddr, 0x00, BluetoothDisconnectReason.Remote);

    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      packet, out var address, out var reason);

    Assert.True(result);
    Assert.Equal("D4:3A:2C:64:87:9E", address);
    Assert.Equal(BluetoothDisconnectReason.Remote, reason);
  }

  [Fact]
  public void TryParseDeviceDisconnected_TimeoutReason_ParsesCorrectly()
  {
    var bdAddr = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
    var packet = BuildDisconnectedEvent(bdAddr, 0x00, BluetoothDisconnectReason.Timeout);

    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      packet, out var address, out var reason);

    Assert.True(result);
    Assert.Equal("06:05:04:03:02:01", address);
    Assert.Equal(BluetoothDisconnectReason.Timeout, reason);
  }

  [Fact]
  public void TryParseDeviceDisconnected_LocalHostReason_ParsesCorrectly()
  {
    var bdAddr = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
    var packet = BuildDisconnectedEvent(bdAddr, 0x01, BluetoothDisconnectReason.LocalHost);

    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      packet, out _, out var reason);

    Assert.True(result);
    Assert.Equal(BluetoothDisconnectReason.LocalHost, reason);
  }

  [Fact]
  public void TryParseDeviceDisconnected_WrongOpcode_ReturnsFalse()
  {
    var packet = BuildDisconnectedEvent(new byte[6], 0x00, BluetoothDisconnectReason.Remote);
    // Change opcode to something else
    packet[0] = 0xFF;
    packet[1] = 0x00;

    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      packet, out _, out _);

    Assert.False(result);
  }

  [Fact]
  public void TryParseDeviceDisconnected_BufferTooShort_ReturnsFalse()
  {
    var packet = new byte[10]; // Too short (need 14)

    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      packet, out _, out _);

    Assert.False(result);
  }

  [Fact]
  public void TryParseDeviceDisconnected_EmptyBuffer_ReturnsFalse()
  {
    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      ReadOnlySpan<byte>.Empty, out _, out _);

    Assert.False(result);
  }

  [Fact]
  public void FormatBdAddr_ValidAddress_FormatsCorrectly()
  {
    // D4:3A:2C:64:87:9E stored in little-endian
    var bdAddr = new byte[] { 0x9E, 0x87, 0x64, 0x2C, 0x3A, 0xD4 };

    var result = BluetoothMgmtEventParser.FormatBdAddr(bdAddr);

    Assert.Equal("D4:3A:2C:64:87:9E", result);
  }

  [Fact]
  public void FormatBdAddr_AllZeros_FormatsCorrectly()
  {
    var bdAddr = new byte[6];

    var result = BluetoothMgmtEventParser.FormatBdAddr(bdAddr);

    Assert.Equal("00:00:00:00:00:00", result);
  }

  [Fact]
  public void FormatBdAddr_TooShort_ReturnsDefault()
  {
    var result = BluetoothMgmtEventParser.FormatBdAddr(new byte[] { 0x01, 0x02 });

    Assert.Equal("00:00:00:00:00:00", result);
  }

  [Theory]
  [InlineData(BluetoothDisconnectReason.Unknown)]
  [InlineData(BluetoothDisconnectReason.AuthFailure)]
  [InlineData(BluetoothDisconnectReason.LocalHostSuspend)]
  public void TryParseDeviceDisconnected_AllReasonCodes_ParseCorrectly(BluetoothDisconnectReason expectedReason)
  {
    var bdAddr = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
    var packet = BuildDisconnectedEvent(bdAddr, 0x00, expectedReason);

    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      packet, out _, out var reason);

    Assert.True(result);
    Assert.Equal(expectedReason, reason);
  }

  [Fact]
  public void TryParseDeviceDisconnected_ExtraTrailingBytes_StillParses()
  {
    var bdAddr = new byte[] { 0x9E, 0x87, 0x64, 0x2C, 0x3A, 0xD4 };
    var basePacket = BuildDisconnectedEvent(bdAddr, 0x00, BluetoothDisconnectReason.Remote);

    // Add extra trailing bytes
    var extended = new byte[basePacket.Length + 50];
    Array.Copy(basePacket, extended, basePacket.Length);

    var result = BluetoothMgmtEventParser.TryParseDeviceDisconnected(
      extended, out var address, out var reason);

    Assert.True(result);
    Assert.Equal("D4:3A:2C:64:87:9E", address);
    Assert.Equal(BluetoothDisconnectReason.Remote, reason);
  }
}
#endif
