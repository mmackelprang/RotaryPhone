using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Audio;

public class MockBluetoothDeviceManager : IBluetoothDeviceManager
{
    private readonly ILogger<MockBluetoothDeviceManager> _logger;

    public MockBluetoothDeviceManager(ILogger<MockBluetoothDeviceManager> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<BluetoothDevice> ConnectedDevices => [];
    public IReadOnlyList<BluetoothDevice> PairedDevices => [];
    public bool IsAdapterReady => false;
    public string? AdapterAddress => null;

#pragma warning disable CS0067 // Events never used in mock
    public event Action<BluetoothDevice>? OnDeviceConnected;
    public event Action<BluetoothDevice>? OnDeviceDisconnected;
    public event Action<BluetoothDevice, string>? OnIncomingCall;
    public event Action<BluetoothDevice>? OnCallAnsweredOnPhone;
    public event Action<BluetoothDevice>? OnCallActive;
    public event Action<BluetoothDevice>? OnCallEnded;
    public event Action<BluetoothDevice>? OnScoAudioConnected;
    public event Action<BluetoothDevice>? OnScoAudioDisconnected;
    public event Action<PairingRequest>? OnPairingRequest;
    public event Action<BluetoothDevice>? OnDevicePaired;
    public event Action<BluetoothDevice>? OnDeviceRemoved;
    public event Action<BluetoothDevice>? OnDeviceDiscovered;
#pragma warning restore CS0067

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("MockBluetoothDeviceManager initialized (no real BT)");
        return Task.CompletedTask;
    }

    public Task<bool> AnswerCallAsync(string addr) { _logger.LogInformation("Mock: AnswerCall {Addr}", addr); return Task.FromResult(true); }
    public Task<bool> HangupCallAsync(string addr) { _logger.LogInformation("Mock: Hangup {Addr}", addr); return Task.FromResult(true); }
    public Task<bool> DialAsync(string addr, string num) { _logger.LogInformation("Mock: Dial {Num} on {Addr}", num, addr); return Task.FromResult(true); }
    public Task<bool> ConnectDeviceAsync(string addr) => Task.FromResult(false);
    public Task<bool> DisconnectDeviceAsync(string addr) => Task.FromResult(false);
    public Task StartDiscoveryAsync() => Task.CompletedTask;
    public Task StopDiscoveryAsync() => Task.CompletedTask;
    public Task<bool> PairDeviceAsync(string addr) => Task.FromResult(false);
    public Task<bool> RemoveDeviceAsync(string addr) => Task.FromResult(false);
    public Task<bool> ConfirmPairingAsync(string addr, bool accept) => Task.FromResult(false);
    public Task<bool> SetAdapterAsync(string? alias, bool? discoverable) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
