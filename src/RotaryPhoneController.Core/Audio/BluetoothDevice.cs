namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Snapshot of a Bluetooth device's state. Immutable.
/// </summary>
public record BluetoothDevice(
    string Address,
    string? Name,
    bool IsConnected,
    bool IsPaired,
    bool HasActiveCall,
    bool HasIncomingCall,
    bool HasScoAudio
);

/// <summary>
/// Represents a pairing request from a Bluetooth device.
/// </summary>
public record PairingRequest(
    string Address,
    string? Name,
    string Type,       // "confirmation", "pin", "passkey"
    string? Passkey    // Passkey to display, or null for PIN entry
);
