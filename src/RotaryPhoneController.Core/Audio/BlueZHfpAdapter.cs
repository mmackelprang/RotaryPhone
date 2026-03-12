#if !WINDOWS
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Bluetooth HFP adapter for Linux using BlueZ.
/// RotaryPhone is BT-passive — Radio.API owns the BlueZ agent and adapter configuration.
/// This adapter monitors D-Bus for device connections on already-paired devices
/// and launches a Python HFP Profile1 agent (hfp_monitor.py) to detect call state
/// via RFCOMM AT commands.
/// </summary>
public class BlueZHfpAdapter : IBluetoothHfpAdapter, IDisposable
{
  private readonly ILogger<BlueZHfpAdapter> _logger;
  private readonly string _deviceName;
  private readonly BluetoothMgmtMonitor? _mgmtMonitor;
  private readonly object _stateLock = new();
  private volatile bool _isConnected;
  private volatile bool _callActive;
  private string? _connectedDeviceAddress;
  private AudioRoute _currentRoute = AudioRoute.RotaryPhone;
  private bool _disposed;
  private CancellationTokenSource? _monitorCts;
  private BluetoothDisconnectReason? _lastDisconnectReason;

  // HFP monitor subprocess
  private Process? _hfpMonitor;
  private readonly object _hfpLock = new();
  private bool _hfpMonitorReady;
  private int _hfpRestartCount;
  private const int MaxHfpRestarts = 10;
  private const int HfpRestartDelayMs = 5000;

  public event Action<string>? OnIncomingCall;
  public event Action? OnCallAnsweredOnCellPhone;
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
  /// Initialize: check for already-connected devices, start D-Bus monitoring,
  /// and launch the HFP monitor Python script.
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

      // Launch HFP monitor Python script
      _ = Task.Run(() => RunHfpMonitorAsync(_monitorCts.Token));

      _logger.LogInformation("BlueZ HFP adapter initialized — monitoring for device connections and HFP call state");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to initialize BlueZ HFP adapter");
      throw;
    }
  }

  #region HFP Monitor Subprocess

  /// <summary>
  /// Resolves the path to hfp_monitor.py relative to the application binary.
  /// </summary>
  private static string GetHfpMonitorScriptPath()
  {
    var baseDir = AppContext.BaseDirectory;

    // Check scripts/ subdirectory first (deployed layout)
    var scriptPath = Path.Combine(baseDir, "scripts", "hfp_monitor.py");
    if (File.Exists(scriptPath))
      return scriptPath;

    // Check sibling scripts/ directory (development layout)
    var devPath = Path.Combine(baseDir, "..", "..", "..", "..", "..", "scripts", "hfp_monitor.py");
    var resolved = Path.GetFullPath(devPath);
    if (File.Exists(resolved))
      return resolved;

    return scriptPath; // Return the expected path even if not found (will fail with clear error)
  }

  /// <summary>
  /// Launches and manages the hfp_monitor.py subprocess with auto-restart.
  /// </summary>
  private async Task RunHfpMonitorAsync(CancellationToken ct)
  {
    var scriptPath = GetHfpMonitorScriptPath();

    while (!ct.IsCancellationRequested && _hfpRestartCount < MaxHfpRestarts)
    {
      try
      {
        _logger.LogInformation("Starting HFP monitor script: {ScriptPath} (attempt {Attempt})",
          scriptPath, _hfpRestartCount + 1);

        if (!File.Exists(scriptPath))
        {
          _logger.LogError("HFP monitor script not found at {ScriptPath}. HFP call detection disabled.", scriptPath);
          return;
        }

        var process = Process.Start(new ProcessStartInfo
        {
          FileName = "python3",
          Arguments = scriptPath,
          RedirectStandardOutput = true,
          RedirectStandardInput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
        });

        if (process == null)
        {
          _logger.LogError("Failed to start python3 process for HFP monitor");
          await Task.Delay(HfpRestartDelayMs, ct);
          _hfpRestartCount++;
          continue;
        }

        lock (_hfpLock)
        {
          _hfpMonitor = process;
          _hfpMonitorReady = false;
        }

        _logger.LogInformation("HFP monitor started (pid={Pid})", process.Id);

        // Read stderr in background (for debugging via journalctl)
        _ = Task.Run(() => ReadHfpStderrAsync(process, ct), ct);

        // Read stdout events
        await ReadHfpEventsAsync(process, ct);

        // Process exited
        lock (_hfpLock)
        {
          _hfpMonitor = null;
          _hfpMonitorReady = false;
        }

        if (!process.HasExited)
        {
          try { process.Kill(); } catch { }
        }

        try { await process.WaitForExitAsync(ct); } catch { }
        var exitCode = process.HasExited ? process.ExitCode : -1;
        process.Dispose();

        if (ct.IsCancellationRequested) break;

        _hfpRestartCount++;
        _logger.LogWarning("HFP monitor exited with code {ExitCode}. Restarting in {Delay}ms (attempt {Attempt}/{Max})",
          exitCode, HfpRestartDelayMs, _hfpRestartCount, MaxHfpRestarts);

        await Task.Delay(HfpRestartDelayMs, ct);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error in HFP monitor management loop");
        _hfpRestartCount++;
        if (!ct.IsCancellationRequested)
          await Task.Delay(HfpRestartDelayMs, ct);
      }
    }

    if (_hfpRestartCount >= MaxHfpRestarts)
    {
      _logger.LogError("HFP monitor exceeded max restart attempts ({Max}). HFP call detection disabled.", MaxHfpRestarts);
    }
  }

  /// <summary>
  /// Read JSON events from the HFP monitor's stdout.
  /// </summary>
  private async Task ReadHfpEventsAsync(Process process, CancellationToken ct)
  {
    var reader = process.StandardOutput;

    while (!ct.IsCancellationRequested && !process.HasExited)
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

      if (line == null) break; // EOF
      if (string.IsNullOrWhiteSpace(line)) continue;

      try
      {
        ProcessHfpEvent(line);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Error processing HFP event: {Line}", line);
      }
    }
  }

  /// <summary>
  /// Parse and handle a JSON event from the HFP monitor.
  /// </summary>
  private void ProcessHfpEvent(string jsonLine)
  {
    using var doc = JsonDocument.Parse(jsonLine);
    var root = doc.RootElement;

    if (!root.TryGetProperty("event", out var eventProp))
      return;

    var eventType = eventProp.GetString();
    _logger.LogInformation("HFP event: {Event} — {Json}", eventType, jsonLine);

    switch (eventType)
    {
      case "ready":
        lock (_hfpLock) { _hfpMonitorReady = true; }
        _hfpRestartCount = 0; // Reset on successful startup
        _logger.LogInformation("HFP monitor is ready and waiting for RFCOMM connections");
        break;

      case "ring":
        var number = root.TryGetProperty("number", out var numProp) ? numProp.GetString() : "Unknown";
        _callActive = false; // Reset — call is ringing, not yet active
        _logger.LogInformation("HFP: Incoming call from {Number}", number);
        OnIncomingCall?.Invoke(number ?? "Unknown");
        break;

      case "call_active":
        _callActive = true;
        _logger.LogInformation("HFP: Call answered (on cell phone)");
        OnCallAnsweredOnCellPhone?.Invoke();
        break;

      case "call_ended":
        if (_callActive)
        {
          _callActive = false;
          _logger.LogInformation("HFP: Call ended");
          OnCallEnded?.Invoke();
        }
        else
        {
          // Call ended without being answered (e.g., caller hung up during ring)
          _logger.LogInformation("HFP: Call ended (was not active — unanswered/rejected)");
          OnCallEnded?.Invoke();
        }
        break;

      case "connected":
        var addr = root.TryGetProperty("address", out var addrProp) ? addrProp.GetString() : null;
        _logger.LogInformation("HFP: RFCOMM connected to {Address}", addr);
        break;

      case "disconnected":
        var dAddr = root.TryGetProperty("address", out var dAddrProp) ? dAddrProp.GetString() : null;
        _callActive = false;
        _logger.LogInformation("HFP: RFCOMM disconnected from {Address}", dAddr);
        break;

      case "error":
        var msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "unknown";
        _logger.LogWarning("HFP monitor error: {Message}", msg);
        break;

      default:
        _logger.LogDebug("Unknown HFP event: {Event}", eventType);
        break;
    }
  }

  /// <summary>
  /// Read stderr from the HFP monitor for logging.
  /// </summary>
  private async Task ReadHfpStderrAsync(Process process, CancellationToken ct)
  {
    try
    {
      var reader = process.StandardError;
      while (!ct.IsCancellationRequested && !process.HasExited)
      {
        var line = await reader.ReadLineAsync(ct);
        if (line == null) break;
        _logger.LogDebug("HFP monitor stderr: {Line}", line);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Error reading HFP monitor stderr");
    }
  }

  /// <summary>
  /// Send a JSON command to the HFP monitor via stdin.
  /// </summary>
  private bool SendHfpCommand(object command)
  {
    Process? process;
    lock (_hfpLock)
    {
      process = _hfpMonitor;
      if (process == null || !_hfpMonitorReady)
        return false;
    }

    try
    {
      var json = JsonSerializer.Serialize(command);
      process.StandardInput.WriteLine(json);
      process.StandardInput.Flush();
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to send command to HFP monitor");
      return false;
    }
  }

  #endregion

  #region Device Connection Monitoring

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

  #endregion

  #region IBluetoothHfpAdapter Implementation

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

      var success = SendHfpCommand(new { command = "dial", number = phoneNumber });
      if (success)
      {
        _callActive = true;
        _logger.LogInformation("Call initiation command sent");
      }
      else
        _logger.LogWarning("Call initiation failed — HFP monitor not ready");

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

      var success = SendHfpCommand(new { command = "answer" });
      if (success)
      {
        _callActive = true;
        _logger.LogInformation("Call answer command sent");
        OnAudioRouteChanged?.Invoke(routeAudio);
      }
      else
      {
        _logger.LogWarning("Failed to answer call — HFP monitor not ready");
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

      var success = SendHfpCommand(new { command = "hangup" });
      if (success)
      {
        _callActive = false;
        _logger.LogInformation("Call termination command sent");
      }

      // Do NOT fire OnCallEnded here — that event is for external call-end
      // notifications (remote hangup), not for calls we terminate ourselves.
      // Firing it here creates a feedback loop with CallManager.HangUp().

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

  #endregion

  public void Dispose()
  {
    if (_disposed) return;

    _logger.LogInformation("Disposing BlueZ HFP adapter");
    _monitorCts?.Cancel();
    _monitorCts?.Dispose();

    // Kill HFP monitor process
    lock (_hfpLock)
    {
      if (_hfpMonitor != null && !_hfpMonitor.HasExited)
      {
        try { _hfpMonitor.Kill(); } catch { }
        _hfpMonitor.Dispose();
        _hfpMonitor = null;
      }
    }

    _disposed = true;
  }
}
#endif
