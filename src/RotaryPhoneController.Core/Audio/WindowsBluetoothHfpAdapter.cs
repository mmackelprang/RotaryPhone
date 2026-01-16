#if WINDOWS
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Configuration;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Windows Bluetooth HFP adapter using Windows.Devices.Bluetooth APIs
/// Implements HFP Hands-Free Unit role for connecting to mobile phones
/// </summary>
public class WindowsBluetoothHfpAdapter : IBluetoothHfpAdapter, IDisposable
{
    private readonly ILogger<WindowsBluetoothHfpAdapter> _logger;
    private readonly string _deviceName;
    private bool _isConnected;
    private string? _connectedDeviceAddress;
    private AudioRoute _currentRoute = AudioRoute.RotaryPhone;
    private bool _disposed;
    private CancellationTokenSource? _monitorCts;

    // HFP Hands-Free role UUID: 0000111e-0000-1000-8000-00805f9b34fb
    private static readonly Guid HfpHandsFreeUuid = Guid.Parse("0000111e-0000-1000-8000-00805f9b34fb");

    private RfcommServiceProvider? _serviceProvider;
    private StreamSocketListener? _socketListener;
    private StreamSocket? _connectedSocket;
    private DataReader? _dataReader;
    private DataWriter? _dataWriter;

    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnsweredOnCellPhone;
    public event Action? OnCallEnded;
    public event Action<AudioRoute>? OnAudioRouteChanged;

    public bool IsConnected => _isConnected;
    public string? ConnectedDeviceAddress => _connectedDeviceAddress;

    public WindowsBluetoothHfpAdapter(ILogger<WindowsBluetoothHfpAdapter> logger, AppConfiguration config)
        : this(logger, config.BluetoothDeviceName)
    {
    }

    public WindowsBluetoothHfpAdapter(ILogger<WindowsBluetoothHfpAdapter> logger, string deviceName = "Rotary Phone")
    {
        _logger = logger;
        _deviceName = deviceName;
        _logger.LogInformation("WindowsBluetoothHfpAdapter initializing with device name: {DeviceName}", _deviceName);
    }

    /// <summary>
    /// Initialize the Windows Bluetooth adapter and start advertising HFP service
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing Windows Bluetooth HFP adapter...");

            // Check Bluetooth availability
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);

            if (bluetoothRadio == null)
            {
                throw new InvalidOperationException("No Bluetooth radio found on this device");
            }

            if (bluetoothRadio.State != Windows.Devices.Radios.RadioState.On)
            {
                _logger.LogWarning("Bluetooth radio is not enabled. Current state: {State}", bluetoothRadio.State);
                throw new InvalidOperationException($"Bluetooth radio is not enabled. Current state: {bluetoothRadio.State}");
            }

            _logger.LogInformation("Bluetooth radio found and enabled");

            // Create RFCOMM service provider for HFP
            await SetupRfcommServiceAsync();

            // Start monitoring for connections and call events
            _monitorCts = new CancellationTokenSource();
            _ = Task.Run(() => MonitorBluetoothEventsAsync(_monitorCts.Token));

            _logger.LogInformation("Windows Bluetooth HFP adapter initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Windows Bluetooth HFP adapter");
            throw;
        }
    }

    private async Task SetupRfcommServiceAsync()
    {
        try
        {
            _logger.LogInformation("Setting up RFCOMM service for HFP...");

            var serviceId = RfcommServiceId.FromUuid(HfpHandsFreeUuid);
            _serviceProvider = await RfcommServiceProvider.CreateAsync(serviceId);

            if (_serviceProvider == null)
            {
                throw new InvalidOperationException(
                    "Failed to create RFCOMM service provider. " +
                    "This may require the application to be packaged (MSIX) or run with elevated permissions.");
            }

            _socketListener = new StreamSocketListener();
            _socketListener.ConnectionReceived += OnConnectionReceived;

            await _socketListener.BindServiceNameAsync(
                _serviceProvider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            // Configure SDP attributes for service discovery
            ConfigureSdpAttributes();

            // Start advertising the service
            _serviceProvider.StartAdvertising(_socketListener);

            _logger.LogInformation("RFCOMM service created and advertising as '{DeviceName}'", _deviceName);
            _logger.LogInformation("HFP Service UUID: {Uuid}", HfpHandsFreeUuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup RFCOMM service");
            throw;
        }
    }

    private void ConfigureSdpAttributes()
    {
        if (_serviceProvider == null) return;

        try
        {
            // Set Service Name attribute (0x0100)
            var writer = new DataWriter();
            writer.WriteByte(0x25); // Text string type
            writer.WriteByte((byte)_deviceName.Length);
            writer.WriteString(_deviceName);

            _serviceProvider.SdpRawAttributes.Add(0x0100, writer.DetachBuffer());

            _logger.LogDebug("SDP attributes configured for '{DeviceName}'", _deviceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure SDP attributes");
        }
    }

    private void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        try
        {
            var remoteAddress = args.Socket.Information.RemoteAddress.DisplayName;
            _logger.LogInformation("Bluetooth connection received from {Address}", remoteAddress);

            _connectedSocket = args.Socket;
            _connectedDeviceAddress = remoteAddress;
            _isConnected = true;

            _dataReader = new DataReader(_connectedSocket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial
            };
            _dataWriter = new DataWriter(_connectedSocket.OutputStream);

            // Start reading AT commands from the connected device
            _ = Task.Run(ReadAtCommandsAsync);

            _logger.LogInformation("Device connected successfully: {Address}", remoteAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling incoming connection");
        }
    }

    private async Task ReadAtCommandsAsync()
    {
        try
        {
            while (_isConnected && _dataReader != null)
            {
                var bytesRead = await _dataReader.LoadAsync(1024);
                if (bytesRead == 0)
                {
                    _logger.LogInformation("Connection closed by remote device");
                    HandleDisconnection();
                    break;
                }

                var data = _dataReader.ReadString(bytesRead);
                _logger.LogDebug("Received AT command: {Command}", data.Trim());

                // Process AT commands
                await ProcessAtCommandAsync(data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading AT commands");
            HandleDisconnection();
        }
    }

    private async Task ProcessAtCommandAsync(string command)
    {
        command = command.Trim().ToUpperInvariant();

        // Handle common HFP AT commands from the phone
        if (command.StartsWith("+CIEV"))
        {
            // Call indicator event
            await HandleCallIndicatorAsync(command);
        }
        else if (command == "RING")
        {
            // Incoming call ring
            _logger.LogInformation("Incoming call detected");
            OnIncomingCall?.Invoke("Unknown");
        }
        else if (command.StartsWith("+CLIP"))
        {
            // Calling Line Identification
            var number = ExtractPhoneNumber(command);
            if (!string.IsNullOrEmpty(number))
            {
                _logger.LogInformation("Incoming call from: {Number}", number);
                OnIncomingCall?.Invoke(number);
            }
        }

        // Send OK response for most commands
        await SendAtResponseAsync("OK");
    }

    private async Task HandleCallIndicatorAsync(string command)
    {
        // Parse +CIEV indicator
        // Format: +CIEV: <indicator>,<value>
        // Indicator 1 = call status (0=no call, 1=call active)
        try
        {
            var parts = command.Split(':')[1].Split(',');
            if (parts.Length >= 2)
            {
                var indicator = int.Parse(parts[0].Trim());
                var value = int.Parse(parts[1].Trim());

                if (indicator == 1) // Call status
                {
                    if (value == 0)
                    {
                        _logger.LogInformation("Call ended");
                        OnCallEnded?.Invoke();
                    }
                    else if (value == 1)
                    {
                        _logger.LogInformation("Call active");
                        OnCallAnsweredOnCellPhone?.Invoke();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse call indicator: {Command}", command);
        }

        await Task.CompletedTask;
    }

    private string? ExtractPhoneNumber(string clipCommand)
    {
        // Parse +CLIP: "number",type
        try
        {
            var startQuote = clipCommand.IndexOf('"');
            var endQuote = clipCommand.IndexOf('"', startQuote + 1);
            if (startQuote >= 0 && endQuote > startQuote)
            {
                return clipCommand.Substring(startQuote + 1, endQuote - startQuote - 1);
            }
        }
        catch { }
        return null;
    }

    private void HandleDisconnection()
    {
        _isConnected = false;
        _connectedDeviceAddress = null;

        _dataReader?.Dispose();
        _dataWriter?.Dispose();
        _connectedSocket?.Dispose();

        _dataReader = null;
        _dataWriter = null;
        _connectedSocket = null;

        _logger.LogInformation("Bluetooth device disconnected");
    }

    private async Task MonitorBluetoothEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting Bluetooth event monitoring...");

            while (!cancellationToken.IsCancellationRequested)
            {
                // Monitor for paired device connections using device watcher could be added here
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Bluetooth event monitoring stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Bluetooth event monitoring");
        }
    }

    public async Task<bool> InitiateCallAsync(string phoneNumber)
    {
        try
        {
            _logger.LogInformation("Initiating call to {PhoneNumber} via Bluetooth HFP", phoneNumber);

            if (!_isConnected)
            {
                _logger.LogWarning("Cannot initiate call - no Bluetooth device connected");
                return false;
            }

            // Send ATD command to dial
            var success = await SendAtCommandAsync($"ATD{phoneNumber};");

            if (success)
            {
                _logger.LogInformation("Call initiation command sent");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating call");
            return false;
        }
    }

    public async Task<bool> AnswerCallAsync(AudioRoute routeAudio)
    {
        try
        {
            _logger.LogInformation("Answering call with audio route: {Route}", routeAudio);
            _currentRoute = routeAudio;

            if (!_isConnected)
            {
                _logger.LogWarning("Cannot answer call - no Bluetooth device connected");
                return false;
            }

            // Send ATA command to answer
            var success = await SendAtCommandAsync("ATA");

            if (success)
            {
                _logger.LogInformation("Call answered successfully");
                OnAudioRouteChanged?.Invoke(routeAudio);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error answering call");
            return false;
        }
    }

    public async Task<bool> TerminateCallAsync()
    {
        try
        {
            _logger.LogInformation("Terminating call via Bluetooth HFP");

            if (!_isConnected)
            {
                _logger.LogWarning("Cannot terminate call - no Bluetooth device connected");
                return false;
            }

            // Send AT+CHUP command to hang up
            var success = await SendAtCommandAsync("AT+CHUP");

            if (success)
            {
                _logger.LogInformation("Call terminated successfully");
                OnCallEnded?.Invoke();
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error terminating call");
            return false;
        }
    }

    public Task<bool> SetAudioRouteAsync(AudioRoute route)
    {
        try
        {
            _logger.LogInformation("Changing audio route from {CurrentRoute} to {NewRoute}",
                _currentRoute, route);

            _currentRoute = route;
            OnAudioRouteChanged?.Invoke(route);

            _logger.LogInformation("Audio route changed successfully");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing audio route");
            return Task.FromResult(false);
        }
    }

    private async Task<bool> SendAtCommandAsync(string command)
    {
        try
        {
            if (_dataWriter == null)
            {
                _logger.LogWarning("Cannot send AT command - not connected");
                return false;
            }

            _logger.LogDebug("Sending AT command: {Command}", command);

            _dataWriter.WriteString(command + "\r\n");
            await _dataWriter.StoreAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending AT command: {Command}", command);
            return false;
        }
    }

    private async Task SendAtResponseAsync(string response)
    {
        try
        {
            if (_dataWriter == null) return;

            _dataWriter.WriteString("\r\n" + response + "\r\n");
            await _dataWriter.StoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending AT response: {Response}", response);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _logger.LogInformation("Disposing Windows Bluetooth HFP adapter");

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        _serviceProvider?.StopAdvertising();

        _dataReader?.Dispose();
        _dataWriter?.Dispose();
        _connectedSocket?.Dispose();
        _socketListener?.Dispose();

        _disposed = true;
    }
}
#endif
