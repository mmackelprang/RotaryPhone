using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Platform;
using RotaryPhoneController.Server.Hubs;

namespace RotaryPhoneController.Server.Services;

public class SignalRNotifierService : IHostedService
{
    private readonly PhoneManagerService _phoneManager;
    private readonly IHubContext<RotaryHub> _hubContext;
    private readonly ILogger<SignalRNotifierService> _logger;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly ISipAdapter _sipAdapter;
    private readonly AppConfiguration _config;
    private readonly IBluetoothDeviceManager? _deviceManager;
    private bool _lastBluetoothConnected;

    public SignalRNotifierService(
        PhoneManagerService phoneManager,
        IHubContext<RotaryHub> hubContext,
        ILogger<SignalRNotifierService> logger,
        IBluetoothHfpAdapter bluetoothAdapter,
        ISipAdapter sipAdapter,
        AppConfiguration config,
        IBluetoothDeviceManager? deviceManager = null)
    {
        _phoneManager = phoneManager;
        _hubContext = hubContext;
        _logger = logger;
        _bluetoothAdapter = bluetoothAdapter;
        _sipAdapter = sipAdapter;
        _config = config;
        _deviceManager = deviceManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SignalR Notifier Service");

        // Subscribe to phone state changes
        foreach (var (phoneId, manager) in _phoneManager.GetAllPhones())
        {
            _logger.LogInformation("Subscribing to events for phone: {PhoneId}", phoneId);
            manager.StateChanged += () => OnStateChanged(phoneId, manager);
        }

        // Subscribe to IBluetoothDeviceManager events (multi-device)
        if (_deviceManager != null)
        {
            _deviceManager.OnDeviceConnected += dev =>
                _hubContext.Clients.All.SendAsync("DeviceConnected", dev.Address, dev.Name);
            _deviceManager.OnDeviceDisconnected += dev =>
                _hubContext.Clients.All.SendAsync("DeviceDisconnected", dev.Address);
            _deviceManager.OnDeviceDiscovered += dev =>
                _hubContext.Clients.All.SendAsync("DeviceDiscovered", dev.Address, dev.Name);
            _deviceManager.OnDevicePaired += dev =>
                _hubContext.Clients.All.SendAsync("DevicePaired", dev.Address, dev.Name);
            _deviceManager.OnDeviceRemoved += dev =>
                _hubContext.Clients.All.SendAsync("DeviceRemoved", dev.Address);
            _deviceManager.OnPairingRequest += req =>
                _hubContext.Clients.All.SendAsync("PairingRequest", req.Address, req.Type, req.Passkey);
        }

        // Track initial Bluetooth state
        _lastBluetoothConnected = _bluetoothAdapter.IsConnected;

        // Start monitoring Bluetooth connection changes
        _ = MonitorBluetoothConnectionAsync(cancellationToken);

        return Task.CompletedTask;
    }

    private void OnStateChanged(string phoneId, CallManager manager)
    {
        _logger.LogInformation("Broadcasting state change for {PhoneId}: {State}", phoneId, manager.CurrentState);
        _hubContext.Clients.All.SendAsync("CallStateChanged", phoneId, manager.CurrentState.ToString());

        // Also broadcast IncomingCall with the phone number when ringing
        if (manager.CurrentState == CallState.Ringing && manager.IncomingPhoneNumber != null)
        {
            _logger.LogInformation("Broadcasting IncomingCall for {PhoneId}: {Number}", phoneId, manager.IncomingPhoneNumber);
            _hubContext.Clients.All.SendAsync("IncomingCall", phoneId, manager.IncomingPhoneNumber);
        }
    }

    private async Task MonitorBluetoothConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var currentConnected = _bluetoothAdapter.IsConnected;

                // If connection state changed, broadcast system status
                if (currentConnected != _lastBluetoothConnected)
                {
                    _lastBluetoothConnected = currentConnected;
                    _logger.LogInformation("Bluetooth connection changed: {Connected}", currentConnected);
                    await BroadcastSystemStatusAsync();
                }

                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error monitoring Bluetooth connection");
            }
        }
    }

    private async Task BroadcastSystemStatusAsync()
    {
        var status = new SystemStatus
        {
            Platform = PlatformDetector.CurrentPlatform.ToString(),
            IsRaspberryPi = PlatformDetector.IsRaspberryPi,
            BluetoothEnabled = _config.UseActualBluetoothHfp,
            BluetoothConnected = _bluetoothAdapter.IsConnected,
            BluetoothDeviceAddress = _bluetoothAdapter.ConnectedDeviceAddress,
            SipListening = _sipAdapter.IsListening,
            SipListenAddress = _config.SipListenAddress,
            SipPort = _config.SipPort
        };

        _logger.LogDebug("Broadcasting system status: Bluetooth={Connected}, SIP={Listening}",
            status.BluetoothConnected, status.SipListening);

        await _hubContext.Clients.All.SendAsync("SystemStatusChanged", status);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
