using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Audio;

namespace RotaryPhoneController.Core.Adapters;

/// <summary>
/// ICallAdapter wrapper for the Bluetooth HFP call path.
/// Delegates to IBluetoothDeviceManager (multi-device) or IBluetoothHfpAdapter (legacy).
/// </summary>
public class BluetoothCallAdapter : ICallAdapter
{
    private readonly IBluetoothDeviceManager? _deviceManager;
    private readonly IBluetoothHfpAdapter _hfpAdapter;
    private readonly ILogger<BluetoothCallAdapter> _logger;
    private string? _activeDeviceAddress;

    public CallAdapterMode Mode => CallAdapterMode.BluetoothHfp;
    public bool IsAvailable => _deviceManager?.ConnectedDevices.Count > 0 || _hfpAdapter.IsConnected;

    public event Action<bool>? OnAvailabilityChanged;
    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnswered;
    public event Action? OnCallEnded;
    public event Action<string>? OnDtmfReceived;

    public BluetoothCallAdapter(
        IBluetoothHfpAdapter hfpAdapter,
        ILogger<BluetoothCallAdapter> logger,
        IBluetoothDeviceManager? deviceManager = null)
    {
        _hfpAdapter = hfpAdapter;
        _logger = logger;
        _deviceManager = deviceManager;
    }

    public Task ActivateAsync(CancellationToken ct = default)
    {
        if (_deviceManager != null)
        {
            _deviceManager.OnIncomingCall += (device, number) =>
            {
                _activeDeviceAddress = device.Address;
                OnIncomingCall?.Invoke(number);
            };
            _deviceManager.OnCallAnsweredOnPhone += device => OnCallAnswered?.Invoke();
            _deviceManager.OnCallEnded += device =>
            {
                if (_activeDeviceAddress == device.Address)
                {
                    OnCallEnded?.Invoke();
                    _activeDeviceAddress = null;
                }
            };
            _deviceManager.OnDeviceConnected += _ => OnAvailabilityChanged?.Invoke(IsAvailable);
            _deviceManager.OnDeviceDisconnected += _ => OnAvailabilityChanged?.Invoke(IsAvailable);
        }
        else
        {
            _hfpAdapter.OnIncomingCall += number => OnIncomingCall?.Invoke(number);
            _hfpAdapter.OnCallEnded += () => OnCallEnded?.Invoke();
            _hfpAdapter.OnCallAnsweredOnCellPhone += () => OnCallAnswered?.Invoke();
        }

        _logger.LogInformation("BluetoothCallAdapter activated");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("BluetoothCallAdapter deactivated");
        return Task.CompletedTask;
    }

    public async Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default)
    {
        if (_deviceManager != null)
        {
            var device = _deviceManager.ConnectedDevices.FirstOrDefault();
            if (device == null)
                throw new InvalidOperationException("No BT device connected");
            _activeDeviceAddress = device.Address;
            await _deviceManager.DialAsync(device.Address, e164Number);
            return $"bt-{Guid.NewGuid():N}";
        }
        else
        {
            await _hfpAdapter.InitiateCallAsync(e164Number);
            return $"bt-legacy-{Guid.NewGuid():N}";
        }
    }

    public async Task AnswerCallAsync(CancellationToken ct = default)
    {
        if (_deviceManager != null && _activeDeviceAddress != null)
            await _deviceManager.AnswerCallAsync(_activeDeviceAddress);
        else
            await _hfpAdapter.AnswerCallAsync(AudioRoute.RotaryPhone);
    }

    public async Task HangUpAsync(CancellationToken ct = default)
    {
        if (_deviceManager != null && _activeDeviceAddress != null)
            await _deviceManager.HangupCallAsync(_activeDeviceAddress);
        else
            await _hfpAdapter.TerminateCallAsync();
        _activeDeviceAddress = null;
    }
}
