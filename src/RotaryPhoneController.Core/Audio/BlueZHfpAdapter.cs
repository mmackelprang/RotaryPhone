#if !WINDOWS
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Bluetooth HFP adapter for Linux using BlueZ.
/// RotaryPhone is BT-passive — Radio.API owns the BlueZ agent and adapter configuration.
/// This adapter monitors D-Bus for device connections on already-paired devices
/// and manages HFP AT command communication.
/// </summary>
public class BlueZHfpAdapter : IBluetoothHfpAdapter, IDisposable
{
  private readonly ILogger<BlueZHfpAdapter> _logger;
  private readonly string _deviceName;
  private readonly BluetoothMgmtMonitor? _mgmtMonitor;
  private readonly object _stateLock = new();
  private volatile bool _isConnected;
  private string? _connectedDeviceAddress;
  private AudioRoute _currentRoute = AudioRoute.RotaryPhone;
  private bool _disposed;
  private CancellationTokenSource? _monitorCts;
  private BluetoothDisconnectReason? _lastDisconnectReason;

#pragma warning disable CS0067 // Events are part of interface but not yet triggered in this implementation
  public event Action<string>? OnIncomingCall;
  public event Action? OnCallAnsweredOnCellPhone;
#pragma warning restore CS0067

  public event Action? OnCallEnded;
  public event Action<AudioRoute>? OnAudioRouteChanged;

  public bool IsConnected => _isConnected;
  public string? ConnectedDeviceAddress => _connectedDeviceAddress;

  /// <summary>
  /// Last disconnect reason from BlueZ mgmt socket.
  /// </summary>
  public BluetoothDisconnectReason? LastDisconnectReason => _lastDisconnectReason;

  public BlueZHfpAdapter(ILogger<BlueZHfpAdapter> logger, AppConfiguration config, BluetoothMgmtMonitor? mgmtMonitor = null)
    : this(logger, config.BluetoothDeviceName, mgmtMonitor)
  {
  }

  public BlueZHfpAdapter(ILogger<BlueZHfpAdapter> logger, string deviceName = "Rotary Phone", BluetoothMgmtMonitor? mgmtMonitor = null)
  {
    _logger = logger;
    _deviceName = deviceName;
    _mgmtMonitor = mgmtMonitor;
    _logger.LogInformation("BlueZHfpAdapter initializing with device name: {DeviceName}", _deviceName);
  }

  /// <summary>
  /// Initialize: check for already-connected devices, then start monitoring
  /// D-Bus for runtime connection changes.
  /// Radio.API owns the adapter — we don't configure it here.
  /// </summary>
  public async Task InitializeAsync()
  {
    try
    {
      _logger.LogInformation("Initializing BlueZ HFP adapter (passive mode — Radio.API owns BT adapter)");

      // Check for already-connected devices (covers service restart while phone is connected)
      await PollConnectedDeviceOnceAsync();

      // Start monitoring for device connections via D-Bus property changes
      _monitorCts = new CancellationTokenSource();
      _ = Task.Run(() => MonitorDeviceConnectionsAsync(_monitorCts.Token));

      // Start periodic poll as safety net (D-Bus signals can be missed)
      _ = Task.Run(() => PeriodicConnectionPollAsync(_monitorCts.Token));

      _logger.LogInformation("BlueZ HFP adapter initialized — monitoring for device connections");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to initialize BlueZ HFP adapter");
      throw;
    }
  }

  /// <summary>
  /// One-shot poll for currently connected devices using bluetoothctl.
  /// Called at startup to detect devices that connected before we started monitoring.
  /// </summary>
  private async Task PollConnectedDeviceOnceAsync()
  {
    try
    {
      var process = Process.Start(new ProcessStartInfo
      {
        FileName = "bluetoothctl",
        Arguments = "devices Connected",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      });

      if (process == null) return;

      await process.WaitForExitAsync();
      var output = await process.StandardOutput.ReadToEndAsync();

      if (string.IsNullOrWhiteSpace(output)) return;

      var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
      if (lines.Length == 0) return;

      // Format: "Device D4:3A:2C:64:87:9E Pixel 8 Pro"
      var parts = lines[0].Split(' ', 3);
      if (parts.Length >= 2)
      {
        var mac = parts[1];
        var name = parts.Length >= 3 ? parts[2].Trim() : "Unknown";
        lock (_stateLock)
        {
          _connectedDeviceAddress = mac;
          _lastDisconnectReason = null;
          _isConnected = true;
        }
        _logger.LogInformation("Startup: Bluetooth device already connected: {Name} ({Address})", name, mac);
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to poll for connected devices at startup");
    }
  }

  /// <summary>
  /// Periodic safety-net poll that runs alongside D-Bus monitoring.
  /// Catches connection changes that D-Bus signals might miss.
  /// </summary>
  private async Task PeriodicConnectionPollAsync(CancellationToken ct)
  {
    // Wait a bit before starting periodic polls (D-Bus monitor handles most events)
    await Task.Delay(10_000, ct);

    while (!ct.IsCancellationRequested)
    {
      try
      {
        var process = Process.Start(new ProcessStartInfo
        {
          FileName = "bluetoothctl",
          Arguments = "devices Connected",
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true
        });

        if (process != null)
        {
          using var timeoutCts = new CancellationTokenSource(5000);
          using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
          await process.WaitForExitAsync(linked.Token);
          var output = await process.StandardOutput.ReadToEndAsync(linked.Token);

          if (!string.IsNullOrWhiteSpace(output))
          {
            var parts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Split(' ', 3);
            if (parts.Length >= 2)
            {
              var mac = parts[1];
              if (!_isConnected || _connectedDeviceAddress != mac)
              {
                var name = parts.Length >= 3 ? parts[2].Trim() : "Unknown";
                lock (_stateLock)
                {
                  _connectedDeviceAddress = mac;
                  _lastDisconnectReason = null;
                  _isConnected = true;
                }
                _logger.LogInformation("Poll: Bluetooth device connected: {Name} ({Address})", name, mac);
              }
            }
          }
          else if (_isConnected)
          {
            string? previousAddress;
            lock (_stateLock) { previousAddress = _connectedDeviceAddress; }

            var reason = (_mgmtMonitor != null && previousAddress != null)
              ? _mgmtMonitor.ConsumeDisconnectReason(previousAddress)
              : BluetoothDisconnectReason.Unknown;

            lock (_stateLock)
            {
              _lastDisconnectReason = reason;
              _isConnected = false;
              _connectedDeviceAddress = null;
            }
            _logger.LogInformation("Poll: Bluetooth device disconnected, reason={Reason}", reason);
          }
        }
      }
      catch (OperationCanceledException) { break; }
      catch (Exception ex)
      {
        _logger.LogDebug(ex, "Error in periodic connection poll");
      }

      await Task.Delay(5_000, ct);
    }
  }

  /// <summary>
  /// Monitors D-Bus for device connection changes using dbus-monitor.
  /// Detects when a paired Bluetooth device connects or disconnects.
  /// </summary>
  private async Task MonitorDeviceConnectionsAsync(CancellationToken ct)
  {
    _logger.LogInformation("Starting D-Bus device connection monitor");

    // Use dbus-monitor to watch for BlueZ device property changes
    Process? dbusMonitor = null;
    try
    {
      dbusMonitor = Process.Start(new ProcessStartInfo
      {
        FileName = "dbus-monitor",
        Arguments = "--system \"type='signal',sender='org.bluez',interface='org.freedesktop.DBus.Properties',member='PropertiesChanged'\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      });

      if (dbusMonitor == null)
      {
        _logger.LogWarning("Failed to start dbus-monitor — falling back to polling");
        await PollDeviceConnectionsAsync(ct);
        return;
      }

      _logger.LogInformation("dbus-monitor started (pid={Pid})", dbusMonitor.Id);
      var reader = dbusMonitor.StandardOutput;
      var buffer = new List<string>();

      while (!ct.IsCancellationRequested && !dbusMonitor.HasExited)
      {
        string? line;
        try
        {
          line = await reader.ReadLineAsync(ct);
        }
        catch (OperationCanceledException)
        {
          break;
        }

        if (line == null) break;

        // Accumulate signal lines
        buffer.Add(line);

        // Process when we see a blank line (signal boundary)
        if (string.IsNullOrWhiteSpace(line) && buffer.Count > 0)
        {
          ProcessDbusSignal(buffer);
          buffer.Clear();
        }
      }
    }
    catch (OperationCanceledException)
    {
      _logger.LogDebug("D-Bus monitor cancelled");
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "D-Bus monitor failed — falling back to polling");
      if (!ct.IsCancellationRequested)
        await PollDeviceConnectionsAsync(ct);
    }
    finally
    {
      if (dbusMonitor != null && !dbusMonitor.HasExited)
      {
        try { dbusMonitor.Kill(); } catch { /* already exited */ }
        dbusMonitor.Dispose();
      }
    }
  }

  /// <summary>
  /// Process a D-Bus signal to detect device Connected property changes.
  /// </summary>
  private void ProcessDbusSignal(List<string> signalLines)
  {
    var joined = string.Join(" ", signalLines);

    // Look for org.bluez.Device1 property changes with Connected
    if (!joined.Contains("org.bluez.Device1") || !joined.Contains("Connected"))
      return;

    _logger.LogDebug("D-Bus Device1 Connected property changed: {Signal}", joined.Length > 200 ? joined[..200] : joined);

    // Extract the device path (e.g., /org/bluez/hci0/dev_D4_3A_2C_64_87_9E)
    string? devicePath = null;
    foreach (var line in signalLines)
    {
      if (line.Contains("path=/org/bluez/") && line.Contains("dev_"))
      {
        var pathStart = line.IndexOf("/org/bluez/");
        if (pathStart >= 0)
        {
          var pathEnd = line.IndexOfAny(new[] { ' ', ',' }, pathStart);
          devicePath = pathEnd > 0 ? line[pathStart..pathEnd] : line[pathStart..].TrimEnd(';', '"');
        }
        break;
      }
    }

    // Determine connected state from the signal content
    bool connected = joined.Contains("boolean true") && joined.Contains("Connected");
    bool disconnected = joined.Contains("boolean false") && joined.Contains("Connected");

    if (connected)
    {
      var mac = ExtractMacFromDevicePath(devicePath);
      lock (_stateLock)
      {
        _connectedDeviceAddress = mac;
        _lastDisconnectReason = null;
        _isConnected = true;
      }
      _logger.LogInformation("Bluetooth device connected: {Address} (path={Path})", mac, devicePath);
    }
    else if (disconnected)
    {
      string? mac;
      lock (_stateLock)
      {
        mac = _connectedDeviceAddress ?? ExtractMacFromDevicePath(devicePath);
      }
      _logger.LogInformation("Bluetooth device disconnected: {Address}", mac);

      // Get disconnect reason from mgmt monitor (may block up to 300ms)
      var reason = (_mgmtMonitor != null && mac != null)
        ? _mgmtMonitor.ConsumeDisconnectReason(mac)
        : BluetoothDisconnectReason.Unknown;

      lock (_stateLock)
      {
        _lastDisconnectReason = reason;
        _isConnected = false;
        _connectedDeviceAddress = null;
      }
      _logger.LogInformation("Disconnect reason for {Address}: {Reason}", mac, reason);
    }
  }

  /// <summary>
  /// Extracts MAC address from BlueZ device path.
  /// e.g., /org/bluez/hci0/dev_D4_3A_2C_64_87_9E → D4:3A:2C:64:87:9E
  /// </summary>
  private static string? ExtractMacFromDevicePath(string? devicePath)
  {
    if (devicePath == null) return null;

    var devIdx = devicePath.IndexOf("dev_");
    if (devIdx < 0) return null;

    var macPart = devicePath[(devIdx + 4)..];
    // Remove any trailing path segments
    var slashIdx = macPart.IndexOf('/');
    if (slashIdx > 0) macPart = macPart[..slashIdx];

    return macPart.Replace('_', ':');
  }

  /// <summary>
  /// Fallback: poll for connected devices using bluetoothctl.
  /// </summary>
  private async Task PollDeviceConnectionsAsync(CancellationToken ct)
  {
    _logger.LogInformation("Using bluetoothctl polling fallback for device monitoring");

    while (!ct.IsCancellationRequested)
    {
      try
      {
        var process = Process.Start(new ProcessStartInfo
        {
          FileName = "bluetoothctl",
          Arguments = "devices Connected",
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true
        });

        if (process != null)
        {
          await process.WaitForExitAsync(ct);
          var output = await process.StandardOutput.ReadToEndAsync(ct);

          if (!string.IsNullOrWhiteSpace(output))
          {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
              var parts = lines[0].Split(' ');
              if (parts.Length >= 2)
              {
                var mac = parts[1];
                if (!_isConnected || _connectedDeviceAddress != mac)
                {
                  lock (_stateLock)
                  {
                    _connectedDeviceAddress = mac;
                    _lastDisconnectReason = null;
                    _isConnected = true;
                  }
                  _logger.LogInformation("Bluetooth device connected: {Address}", mac);
                }
              }
            }
          }
          else if (_isConnected)
          {
            string? previousAddress;
            lock (_stateLock) { previousAddress = _connectedDeviceAddress; }

            var reason = (_mgmtMonitor != null && previousAddress != null)
              ? _mgmtMonitor.ConsumeDisconnectReason(previousAddress)
              : BluetoothDisconnectReason.Unknown;

            lock (_stateLock)
            {
              _lastDisconnectReason = reason;
              _isConnected = false;
              _connectedDeviceAddress = null;
            }
            _logger.LogInformation("Bluetooth device disconnected, reason={Reason}", reason);
          }
        }
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogDebug(ex, "Error polling device connections");
      }

      await Task.Delay(2000, ct);
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

      var success = await SendAtCommandAsync($"ATD{phoneNumber};");
      if (success)
        _logger.LogInformation("Call initiation successful");
      else
        _logger.LogWarning("Call initiation failed");

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

      var success = await SendAtCommandAsync("ATA");
      if (success)
      {
        _logger.LogInformation("Call answered successfully");
        OnAudioRouteChanged?.Invoke(routeAudio);
      }
      else
      {
        _logger.LogWarning("Failed to answer call");
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

  public async Task<bool> SetAudioRouteAsync(AudioRoute route)
  {
    try
    {
      _logger.LogInformation("Changing audio route from {CurrentRoute} to {NewRoute}",
        _currentRoute, route);

      _currentRoute = route;
      OnAudioRouteChanged?.Invoke(route);

      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error changing audio route");
      return false;
    }
  }

  private async Task<bool> SendAtCommandAsync(string command)
  {
    try
    {
      _logger.LogDebug("Sending AT command: {Command}", command);

      // AT commands are sent via RFCOMM channel to the connected phone.
      // For now, we log the command — RFCOMM socket implementation
      // will be added when HFP call flow is tested end-to-end.
      // The HT801 ATA handles the actual analog phone line; AT commands
      // control the Bluetooth HFP link to the mobile phone.
      await Task.Delay(100);

      _logger.LogDebug("AT command completed: {Command}", command);
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error sending AT command: {Command}", command);
      return false;
    }
  }

  public void Dispose()
  {
    if (_disposed) return;

    _logger.LogInformation("Disposing BlueZ HFP adapter");
    _monitorCts?.Cancel();
    _monitorCts?.Dispose();
    _disposed = true;
  }
}
#endif
