using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Manages bt_manager.py subprocess and processes JSON events.
/// Not platform-gated — event processing is platform-agnostic.
/// The subprocess simply won't start on Windows (python3/bt_manager.py not present).
/// </summary>
public class BlueZBtManager : IBluetoothDeviceManager
{
    private readonly ILogger<BlueZBtManager> _logger;
    private readonly AppConfiguration _config;
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new();
    private readonly HashSet<string> _pendingAnswers = new(); // devices we sent ATA to
    private readonly object _answerLock = new();

    private Process? _process;
    private CancellationTokenSource? _cts;
    private bool _adapterReady;
    private string? _adapterAddress;
    private int _restartCount;
    private const int MaxRestarts = 10;
    private const int RestartDelayMs = 5000;

    // Events
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

    public IReadOnlyList<BluetoothDevice> ConnectedDevices =>
        _devices.Values.Where(d => d.IsConnected).Select(d => d.ToRecord()).ToList();

    public IReadOnlyList<BluetoothDevice> PairedDevices =>
        _devices.Values.Where(d => d.IsPaired).Select(d => d.ToRecord()).ToList();

    public bool IsAdapterReady => _adapterReady;
    public string? AdapterAddress => _adapterAddress;

    public BlueZBtManager(ILogger<BlueZBtManager> logger, AppConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RunProcessLoopAsync(_cts.Token));
        _logger.LogInformation("BlueZBtManager initialized, starting bt_manager.py");
    }

    /// <summary>Exposed for testing — processes a single JSON event line.</summary>
    internal void ProcessEventForTest(string jsonLine) => ProcessEvent(jsonLine);

    /// <summary>Exposed for testing — marks that we sent ATA to a device.</summary>
    internal void MarkAnswerSent(string address)
    {
        lock (_answerLock) { _pendingAnswers.Add(address); }
    }

    private void ProcessEvent(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;
        if (!root.TryGetProperty("event", out var evtProp)) return;
        var evt = evtProp.GetString();
        var addr = root.TryGetProperty("address", out var ap) ? ap.GetString() : null;

        _logger.LogInformation("BT event: {Event} addr={Address}", evt, addr);

        switch (evt)
        {
            case "adapter_ready":
                _adapterReady = true;
                _adapterAddress = addr;
                _restartCount = 0;
                _logger.LogInformation("BT adapter ready: {Address}", addr);
                break;

            case "ready":
                _adapterReady = true;
                _restartCount = 0;
                break;

            case "connected":
            {
                var name = root.TryGetProperty("name", out var np) ? np.GetString() : null;
                var state = GetOrAdd(addr!);
                state.IsConnected = true;
                state.Name = name ?? state.Name;
                var record = state.ToRecord();
                OnDeviceConnected?.Invoke(record);
                break;
            }

            case "disconnected":
            {
                if (addr != null && _devices.TryGetValue(addr, out var state))
                {
                    state.IsConnected = false;
                    state.HasActiveCall = false;
                    state.HasIncomingCall = false;
                    state.HasScoAudio = false;
                    lock (_answerLock) { _pendingAnswers.Remove(addr); }
                    OnDeviceDisconnected?.Invoke(state.ToRecord());
                }
                break;
            }

            case "ring":
            {
                var number = root.TryGetProperty("number", out var np) ? np.GetString() ?? "Unknown" : "Unknown";
                var state = GetOrAdd(addr!);
                state.HasIncomingCall = true;
                OnIncomingCall?.Invoke(state.ToRecord(), number);
                break;
            }

            case "call_active":
            {
                var state = GetOrAdd(addr!);
                state.HasActiveCall = true;
                state.HasIncomingCall = false;

                // Check if WE sent ATA — if not, the user answered on the phone
                bool weSentAnswer;
                lock (_answerLock) { weSentAnswer = _pendingAnswers.Remove(addr!); }

                // Always fire OnCallActive (used for Dialing→InCall on outgoing calls)
                OnCallActive?.Invoke(state.ToRecord());

                if (!weSentAnswer)
                {
                    // We didn't send ATA, so the user answered on the phone.
                    // Audio stays on phone — no SCO bridge needed.
                    OnCallAnsweredOnPhone?.Invoke(state.ToRecord());
                }
                break;
            }

            case "call_ended":
            {
                if (addr != null && _devices.TryGetValue(addr, out var state))
                {
                    state.HasActiveCall = false;
                    state.HasIncomingCall = false;
                    lock (_answerLock) { _pendingAnswers.Remove(addr); }
                    OnCallEnded?.Invoke(state.ToRecord());
                }
                break;
            }

            case "sco_connected":
            {
                var state = GetOrAdd(addr!);
                state.HasScoAudio = true;
                OnScoAudioConnected?.Invoke(state.ToRecord());
                break;
            }

            case "sco_disconnected":
            {
                if (addr != null && _devices.TryGetValue(addr, out var state))
                {
                    state.HasScoAudio = false;
                    OnScoAudioDisconnected?.Invoke(state.ToRecord());
                }
                break;
            }

            case "device_discovered":
            {
                var name = root.TryGetProperty("name", out var np) ? np.GetString() : null;
                var paired = root.TryGetProperty("paired", out var pp) && pp.GetBoolean();
                var dev = new BluetoothDevice(addr!, name, false, paired, false, false, false);
                OnDeviceDiscovered?.Invoke(dev);
                break;
            }

            case "device_paired":
            {
                var state = GetOrAdd(addr!);
                state.IsPaired = true;
                state.Name = root.TryGetProperty("name", out var np) ? np.GetString() : state.Name;
                OnDevicePaired?.Invoke(state.ToRecord());
                break;
            }

            case "device_removed":
            {
                if (addr != null)
                {
                    _devices.TryRemove(addr, out var removed);
                    OnDeviceRemoved?.Invoke(new BluetoothDevice(addr, removed?.Name, false, false, false, false, false));
                }
                break;
            }

            case "pairing_request":
            {
                var type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "confirmation" : "confirmation";
                var passkey = root.TryGetProperty("passkey", out var pk) ? pk.GetString() : null;
                var name = root.TryGetProperty("name", out var np) ? np.GetString() : null;
                OnPairingRequest?.Invoke(new PairingRequest(addr!, name, type, passkey));
                break;
            }

            case "error":
                var msg = root.TryGetProperty("message", out var mp) ? mp.GetString() : "unknown";
                _logger.LogWarning("bt_manager error: {Message}", msg);
                break;
        }
    }

    private DeviceState GetOrAdd(string address)
    {
        return _devices.GetOrAdd(address, a => new DeviceState { Address = a });
    }

    #region Commands

    private bool SendCommand(object cmd)
    {
        var proc = _process;
        if (proc == null || proc.HasExited) return false;
        try
        {
            var json = JsonSerializer.Serialize(cmd);
            proc.StandardInput.WriteLine(json);
            proc.StandardInput.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send command to bt_manager");
            return false;
        }
    }

    public Task<bool> AnswerCallAsync(string addr)
    {
        lock (_answerLock) { _pendingAnswers.Add(addr); }
        return Task.FromResult(SendCommand(new { command = "answer", address = addr }));
    }

    public Task<bool> HangupCallAsync(string addr) =>
        Task.FromResult(SendCommand(new { command = "hangup", address = addr }));

    public Task<bool> DialAsync(string addr, string number)
    {
        lock (_answerLock) { _pendingAnswers.Add(addr); } // outgoing = we initiated
        return Task.FromResult(SendCommand(new { command = "dial", address = addr, number }));
    }

    public Task<bool> ConnectDeviceAsync(string addr) =>
        Task.FromResult(SendCommand(new { command = "connect", address = addr }));

    public Task<bool> DisconnectDeviceAsync(string addr) =>
        Task.FromResult(SendCommand(new { command = "disconnect", address = addr }));

    public Task StartDiscoveryAsync()
    {
        SendCommand(new { command = "start_discovery" });
        return Task.CompletedTask;
    }

    public Task StopDiscoveryAsync()
    {
        SendCommand(new { command = "stop_discovery" });
        return Task.CompletedTask;
    }

    public Task<bool> PairDeviceAsync(string addr) =>
        Task.FromResult(SendCommand(new { command = "pair", address = addr }));

    public Task<bool> RemoveDeviceAsync(string addr) =>
        Task.FromResult(SendCommand(new { command = "remove_device", address = addr }));

    public Task<bool> ConfirmPairingAsync(string addr, bool accept) =>
        Task.FromResult(SendCommand(new { command = "confirm_pairing", address = addr, accept }));

    public Task<bool> SetAdapterAsync(string? alias, bool? discoverable) =>
        Task.FromResult(SendCommand(new { command = "set_adapter", alias, discoverable }));

    #endregion

    #region Process Lifecycle

    private async Task RunProcessLoopAsync(CancellationToken ct)
    {
        var scriptPath = FindScript();
        while (!ct.IsCancellationRequested && _restartCount < MaxRestarts)
        {
            try
            {
                if (!File.Exists(scriptPath))
                {
                    _logger.LogError("bt_manager.py not found at {Path}", scriptPath);
                    return;
                }

                var adapterArg = _config.BluetoothAdapter != null
                    ? $" --adapter /org/bluez/{_config.BluetoothAdapter}"
                    : "";
                var aliasArg = $" --alias \"{_config.BluetoothAdapterAlias}\"";

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"{scriptPath}{adapterArg}{aliasArg}",
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (proc == null)
                {
                    _restartCount++;
                    await Task.Delay(RestartDelayMs, ct);
                    continue;
                }

                _process = proc;
                _logger.LogInformation("bt_manager.py started (pid={Pid})", proc.Id);

                _ = Task.Run(() => ReadStderrAsync(proc, ct), ct);
                await ReadEventsAsync(proc, ct);

                _process = null;
                _adapterReady = false;

                if (!proc.HasExited) try { proc.Kill(); } catch { }
                try { await proc.WaitForExitAsync(ct); } catch { }
                proc.Dispose();

                if (ct.IsCancellationRequested) break;

                _restartCount++;
                _logger.LogWarning("bt_manager.py exited, restarting ({Attempt}/{Max})",
                    _restartCount, MaxRestarts);
                await Task.Delay(RestartDelayMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "bt_manager process loop error");
                _restartCount++;
                if (!ct.IsCancellationRequested)
                    await Task.Delay(RestartDelayMs, ct);
            }
        }
    }

    private async Task ReadEventsAsync(Process proc, CancellationToken ct)
    {
        var reader = proc.StandardOutput;
        while (!ct.IsCancellationRequested && !proc.HasExited)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { ProcessEvent(line); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error processing event: {Line}", line); }
        }
    }

    private async Task ReadStderrAsync(Process proc, CancellationToken ct)
    {
        try
        {
            var reader = proc.StandardError;
            while (!ct.IsCancellationRequested && !proc.HasExited)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                _logger.LogInformation("bt_manager stderr: {Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Error reading bt_manager stderr"); }
    }

    private static string FindScript()
    {
        var baseDir = AppContext.BaseDirectory;
        var deployed = Path.Combine(baseDir, "scripts", "bt_manager.py");
        if (File.Exists(deployed)) return deployed;
        var dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "scripts", "bt_manager.py"));
        if (File.Exists(dev)) return dev;
        return deployed;
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var proc = _process;
        if (proc != null && !proc.HasExited)
        {
            try { proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    /// <summary>Mutable device state tracked internally.</summary>
    private class DeviceState
    {
        public required string Address { get; init; }
        public string? Name { get; set; }
        public bool IsConnected { get; set; }
        public bool IsPaired { get; set; }
        public bool HasActiveCall { get; set; }
        public bool HasIncomingCall { get; set; }
        public bool HasScoAudio { get; set; }

        public BluetoothDevice ToRecord() =>
            new(Address, Name, IsConnected, IsPaired, HasActiveCall, HasIncomingCall, HasScoAudio);
    }
}
