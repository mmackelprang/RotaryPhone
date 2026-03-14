namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Manages Bluetooth devices, HFP connections, and pairing.
/// All events may fire from background threads (Python subprocess reader).
/// Implementations must be thread-safe. Device list properties return snapshots.
/// </summary>
public interface IBluetoothDeviceManager : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // Device tracking — returns snapshot copies
    IReadOnlyList<BluetoothDevice> ConnectedDevices { get; }
    IReadOnlyList<BluetoothDevice> PairedDevices { get; }

    // Device events
    event Action<BluetoothDevice>? OnDeviceConnected;
    event Action<BluetoothDevice>? OnDeviceDisconnected;

    // Call events — include device for multi-phone routing
    /// <param name="device">The device receiving the call.</param>
    /// <param name="phoneNumber">Caller's number, or "Unknown".</param>
    event Action<BluetoothDevice, string>? OnIncomingCall;
    /// <summary>Call answered on the cell phone (not rotary). Audio stays on phone.</summary>
    event Action<BluetoothDevice>? OnCallAnsweredOnPhone;
    /// <summary>Call became active (answered by anyone). Used for Dialing→InCall on outgoing calls.</summary>
    event Action<BluetoothDevice>? OnCallActive;
    event Action<BluetoothDevice>? OnCallEnded;

    // SCO audio events — drive the audio bridge lifecycle
    event Action<BluetoothDevice>? OnScoAudioConnected;
    event Action<BluetoothDevice>? OnScoAudioDisconnected;

    // Pairing events
    event Action<PairingRequest>? OnPairingRequest;
    event Action<BluetoothDevice>? OnDevicePaired;
    event Action<BluetoothDevice>? OnDeviceRemoved;
    event Action<BluetoothDevice>? OnDeviceDiscovered;

    // Call commands
    Task<bool> AnswerCallAsync(string deviceAddress);
    Task<bool> HangupCallAsync(string deviceAddress);
    Task<bool> DialAsync(string deviceAddress, string phoneNumber);

    // Connection commands
    Task<bool> ConnectDeviceAsync(string deviceAddress);
    Task<bool> DisconnectDeviceAsync(string deviceAddress);

    // Discovery & pairing
    Task StartDiscoveryAsync();
    Task StopDiscoveryAsync();
    Task<bool> PairDeviceAsync(string deviceAddress);
    Task<bool> RemoveDeviceAsync(string deviceAddress);
    Task<bool> ConfirmPairingAsync(string deviceAddress, bool accept);

    /// <summary>Configure adapter. Null parameters are ignored.</summary>
    Task<bool> SetAdapterAsync(string? alias, bool? discoverable);

    bool IsAdapterReady { get; }
    string? AdapterAddress { get; }
}
