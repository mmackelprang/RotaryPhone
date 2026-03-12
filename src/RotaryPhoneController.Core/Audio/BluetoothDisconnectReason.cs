namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Disconnect reason from BlueZ management protocol (MGMT_EV_DEVICE_DISCONNECTED).
/// Values match kernel mgmt.h MGMT_DEV_DISCONN_* constants.
/// </summary>
public enum BluetoothDisconnectReason : byte
{
  /// <summary>Unknown reason (default / fallback).</summary>
  Unknown = 0x00,

  /// <summary>Connection timed out (device went out of range).</summary>
  Timeout = 0x01,

  /// <summary>Disconnected by local host (our service initiated).</summary>
  LocalHost = 0x02,

  /// <summary>Disconnected by remote device (phone user disconnected).</summary>
  Remote = 0x03,

  /// <summary>Authentication failure.</summary>
  AuthFailure = 0x04,

  /// <summary>Local host suspended.</summary>
  LocalHostSuspend = 0x05,
}

public static class BluetoothDisconnectReasonExtensions
{
  /// <summary>
  /// Whether this disconnect reason should suppress auto-reconnect.
  /// Remote, LocalHost, AuthFailure, and LocalHostSuspend all suppress.
  /// Only Timeout and Unknown allow reconnect.
  /// </summary>
  public static bool ShouldSuppressReconnect(this BluetoothDisconnectReason reason) =>
    reason is BluetoothDisconnectReason.Remote
      or BluetoothDisconnectReason.LocalHost
      or BluetoothDisconnectReason.AuthFailure
      or BluetoothDisconnectReason.LocalHostSuspend;
}
