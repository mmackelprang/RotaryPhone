# RotaryPhone Standalone Architecture Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform RotaryPhone into a standalone system connecting 1-2 cell phones to a rotary phone via Bluetooth HFP with full bidirectional voice audio, pairing UI, and correct audio routing.

**Architecture:** Python bt_manager.py handles BlueZ D-Bus (adapter config, HFP Profile1, pairing agent, SCO audio) and communicates with .NET via JSON stdin/stdout. .NET manages call state (CallManager), SIP/RTP (SIPSorcery + G711Codec), and serves the React UI + SignalR hub. SCO audio bridges to RTP via local UDP.

**Tech Stack:** .NET 10 (C#), Python 3.12 (dbus-python, PyGObject), BlueZ 5.72, SIPSorcery, React 19, MUI 7, SignalR, xUnit + Moq.

**Spec:** `docs/superpowers/specs/2026-03-13-rotaryphone-standalone-architecture-design.md`

---

## File Structure

### New Files
| File | Responsibility |
|------|---------------|
| `scripts/bt_manager.py` | Full BT manager: adapter config, HFP Profile1, multi-device RFCOMM, BlueZ Agent1, SCO audio, discovery |
| `src/RotaryPhoneController.Core/Audio/IBluetoothDeviceManager.cs` | Multi-device BT interface replacing IBluetoothHfpAdapter |
| `src/RotaryPhoneController.Core/Audio/BluetoothDevice.cs` | Device record + PairingRequest record |
| `src/RotaryPhoneController.Core/Audio/BlueZBtManager.cs` | .NET wrapper managing bt_manager.py subprocess |
| `src/RotaryPhoneController.Core/Audio/MockBluetoothDeviceManager.cs` | Mock for Windows dev / testing |
| `src/RotaryPhoneController.Core/Audio/ScoRtpBridge.cs` | SCO↔RTP audio bridge via local UDP |
| `src/RotaryPhoneController.Server/Controllers/BluetoothController.cs` | REST API for BT device management |
| `src/RotaryPhoneController.Client/src/pages/Pairing.tsx` | React pairing/device management page |
| `src/RotaryPhoneController.Tests/BlueZBtManagerTests.cs` | Tests for BT manager event processing |
| `src/RotaryPhoneController.Tests/ScoRtpBridgeTests.cs` | Tests for SCO bridge |

### Modified Files
| File | Changes |
|------|---------|
| `scripts/hfp_monitor.py` | Phase 1: fix callsetup=1 ring event |
| `src/.../Audio/BlueZHfpAdapter.cs` | Phase 2: add adapter path config. Phase 3: deprecated |
| `src/.../Configuration/AppConfiguration.cs` | Add BluetoothAdapter, MaxConnectedPhones, ScoUdpPort* |
| `src/.../ISipAdapter.cs` | Add CancelPendingInviteAsync() |
| `src/.../SIPSorceryAdapter.cs` | Implement CancelPendingInviteAsync() |
| `src/.../CallManager.cs` | Device-specific calls, deferred InCall, AnsweredOn gating |
| `src/.../PhoneManagerService.cs` | Wire IBluetoothDeviceManager |
| `src/.../Services/SignalRNotifierService.cs` | BT device events broadcast |
| `src/.../Hubs/RotaryHub.cs` | BT management hub methods |
| `src/.../Program.cs` | DI for IBluetoothDeviceManager, BluetoothController |
| `src/.../Client/src/App.tsx` | Add /pairing route |
| `src/.../Client/src/components/Layout.tsx` | Add Pairing nav link |
| `deploy/rotary-phone.service` | Verify CAP_NET_ADMIN |
| `appsettings.json` | New BT config options |

---

## Chunk 1: Phase 1 — Bug Fixes

### Task 1: Fix callsetup=1 ring detection in hfp_monitor.py

**Files:**
- Modify: `scripts/hfp_monitor.py:332-343`

The phone sends `+CIEV: callsetup=1` for incoming calls. Currently only `RING` AT command triggers the ring event. Many phones send `+CIEV: callsetup=1` before (or instead of) explicit `RING`.

- [ ] **Step 1: Add ring emission on callsetup=1**

In `_handle_ciev()`, when `callsetup` indicator transitions to 1 (incoming call setup), emit a ring event:

```python
        elif ind_index == callsetup_idx:
            prev_callsetup = self.callsetup
            self.callsetup = ind_value

            if ind_value == 1:
                # Incoming call setup — emit ring event
                # Number may come later via +CLIP, so use what we have
                number = self.clip_number or "Unknown"
                emit({"event": "ring", "number": number})
                log(f"Incoming call setup — ring (number: {number})")
            elif ind_value == 0 and prev_callsetup == 1 and not self.call_active:
                # Call setup ended without answering (caller hung up / rejected)
                self.clip_number = None
                emit({"event": "call_ended"})
                log("Call setup ended (unanswered)")
```

- [ ] **Step 2: Test manually**

Deploy to radio and call the Pixel 8 Pro. Check journalctl for `ring` event emission.

Run: `ssh mmack@radio "sudo journalctl -u rotary-phone.service --since '5 minutes ago' --no-pager | grep -i 'ring\|callsetup\|HFP event'"`

Expected: Log line containing `HFP event: ring` when phone is called.

- [ ] **Step 3: Commit**

```bash
git add scripts/hfp_monitor.py
git commit -m "fix: emit ring event on +CIEV callsetup=1, not just RING AT command"
```

---

### Task 2: Raise HFP monitor stderr logging to Information

**Files:**
- Modify: `src/RotaryPhoneController.Core/Audio/BlueZHfpAdapter.cs:333`

HFP monitor's stderr is logged at `LogDebug` level, invisible in production (Serilog minimum level is Information).

- [ ] **Step 1: Change LogDebug to LogInformation for stderr**

```csharp
// In ReadHfpStderrAsync, line 333:
// Change:
_logger.LogDebug("HFP monitor stderr: {Line}", line);
// To:
_logger.LogInformation("HFP monitor stderr: {Line}", line);
```

- [ ] **Step 2: Also change the stderr error catch log level**

```csharp
// Line 339:
// Change:
_logger.LogDebug(ex, "Error reading HFP monitor stderr");
// To:
_logger.LogWarning(ex, "Error reading HFP monitor stderr");
```

- [ ] **Step 3: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --verbosity quiet`
Expected: All existing tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Core/Audio/BlueZHfpAdapter.cs
git commit -m "fix: raise HFP monitor stderr logging from Debug to Information"
```

---

### Task 3: Deploy and test Phase 1 fixes

- [ ] **Step 1: Deploy**

Run: `pwsh deploy/Deploy-ToLinux.ps1 -Logs`

- [ ] **Step 2: Test incoming call**

Call the Pixel 8 Pro from another phone. Verify in journalctl:
1. `HFP event: ring` appears
2. SIP INVITE sent to HT801 (if HT801 is registered)
3. SignalR broadcasts Ringing state

- [ ] **Step 3: Check HT801 SIP registration**

Browse to `http://192.168.86.250` and verify:
- SIP Server: set to radio's IP (the host running RotaryPhone)
- SIP Transport: UDP
- Primary SIP Server port: 5060

If misconfigured, update and reboot the HT801.

- [ ] **Step 4: Commit any config fixes**

```bash
git commit -m "fix: Phase 1 bug fixes verified — callsetup ring + HT801 registration"
```

---

## Chunk 2: Phase 2 — Adapter Ownership

### Task 4: Add BluetoothAdapter config option

**Files:**
- Modify: `src/RotaryPhoneController.Core/Configuration/AppConfiguration.cs:37-103`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`

- [ ] **Step 1: Add config properties**

In `AppConfiguration.cs`, add after `BluetoothDeviceName`:

```csharp
/// <summary>
/// BlueZ adapter to use (e.g., "hci1"). Null = default adapter.
/// </summary>
public string? BluetoothAdapter { get; set; }

/// <summary>
/// Alias to set on the BT adapter (visible name during pairing).
/// </summary>
public string BluetoothAdapterAlias { get; set; } = "Rotary Phone";

/// <summary>
/// Maximum number of phones that can be connected simultaneously.
/// </summary>
public int MaxConnectedPhones { get; set; } = 2;

/// <summary>
/// Base UDP port for SCO audio bridge (per-device: base, base+1; base+2, base+3; ...).
/// </summary>
public int ScoUdpBasePort { get; set; } = 49100;
```

- [ ] **Step 2: Update appsettings.json**

Add under the `RotaryPhone` section:

```json
"BluetoothAdapter": null,
"BluetoothAdapterAlias": "Rotary Phone",
"MaxConnectedPhones": 2,
"ScoUdpBasePort": 49100
```

- [ ] **Step 3: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --verbosity quiet`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Core/Configuration/AppConfiguration.cs src/RotaryPhoneController.Server/appsettings.json
git commit -m "feat: add BluetoothAdapter, MaxConnectedPhones, ScoUdpBasePort config"
```

---

### Task 5: Rename hfp_monitor.py → bt_manager.py with adapter config

**Files:**
- Create: `scripts/bt_manager.py` (copy from `scripts/hfp_monitor.py`, then modify)
- Modify: `src/RotaryPhoneController.Core/Audio/BlueZHfpAdapter.cs:102-118`

- [ ] **Step 1: Copy hfp_monitor.py to bt_manager.py**

```bash
cp scripts/hfp_monitor.py scripts/bt_manager.py
```

- [ ] **Step 2: Add adapter path argument to bt_manager.py**

At the top of `main()`, add argument parsing:

```python
import argparse

def main():
    parser = argparse.ArgumentParser(description="RotaryPhone Bluetooth Manager")
    parser.add_argument("--adapter", default=None, help="BlueZ adapter path (e.g., /org/bluez/hci1)")
    parser.add_argument("--alias", default="Rotary Phone", help="Adapter alias for pairing")
    args = parser.parse_args()

    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.SystemBus()

    adapter_path = args.adapter
    if adapter_path:
        # Configure adapter alias and power
        try:
            adapter = dbus.Interface(
                bus.get_object("org.bluez", adapter_path),
                "org.freedesktop.DBus.Properties"
            )
            adapter.Set("org.bluez.Adapter1", "Alias", args.alias)
            adapter.Set("org.bluez.Adapter1", "Powered", dbus.Boolean(True))
            log(f"Configured adapter {adapter_path}: alias={args.alias}, powered=on")
        except dbus.exceptions.DBusException as e:
            log(f"Warning: could not configure adapter {adapter_path}: {e}")
```

- [ ] **Step 3: Filter device scanning to configured adapter**

When scanning for connected devices, filter to the configured adapter path prefix. In the existing connected-device scan loop, add a check:

```python
    # When iterating managed objects for auto-connect:
    for path, interfaces in objects.items():
        if "org.bluez.Device1" not in interfaces:
            continue
        # Filter to our adapter only
        if adapter_path and not path.startswith(adapter_path + "/"):
            continue
        # ... rest of existing logic
```

Apply the same filter to the `on_properties_changed` handler:

```python
    def on_properties_changed(interface, changed, invalidated, path=None):
        if interface != "org.bluez.Device1":
            return
        # Filter to our adapter
        if adapter_path and not path.startswith(adapter_path + "/"):
            return
        # ... rest of existing logic
```

- [ ] **Step 4: Update BlueZHfpAdapter to launch bt_manager.py with --adapter**

In `BlueZHfpAdapter.cs`, update `GetHfpMonitorScriptPath()` to look for `bt_manager.py` first, falling back to `hfp_monitor.py`:

```csharp
private static string GetBtManagerScriptPath()
{
    var baseDir = AppContext.BaseDirectory;

    // Prefer bt_manager.py (new name)
    var btPath = Path.Combine(baseDir, "scripts", "bt_manager.py");
    if (File.Exists(btPath)) return btPath;

    // Fall back to hfp_monitor.py (old name, transitional)
    var hfpPath = Path.Combine(baseDir, "scripts", "hfp_monitor.py");
    if (File.Exists(hfpPath)) return hfpPath;

    // Dev layout
    var devPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "scripts", "bt_manager.py"));
    if (File.Exists(devPath)) return devPath;

    return btPath;
}
```

Update `RunHfpMonitorAsync` to pass `--adapter` argument:

```csharp
// In RunHfpMonitorAsync, when constructing ProcessStartInfo:
var adapterArg = "";
// Get adapter from config if available (passed via constructor)
if (!string.IsNullOrEmpty(_adapterPath))
    adapterArg = $" --adapter /org/bluez/{_adapterPath}";

var process = Process.Start(new ProcessStartInfo
{
    FileName = "python3",
    Arguments = $"{scriptPath}{adapterArg}",
    // ... rest unchanged
});
```

- [ ] **Step 5: Update BlueZHfpAdapter constructor to accept adapter path**

Add `_adapterPath` field and accept it from `AppConfiguration`:

```csharp
private readonly string? _adapterPath;

public BlueZHfpAdapter(ILogger<BlueZHfpAdapter> logger, AppConfiguration config, BluetoothMgmtMonitor? mgmtMonitor = null)
    : this(logger, config.BluetoothDeviceName, config.BluetoothAdapter, mgmtMonitor)
{
}

public BlueZHfpAdapter(ILogger<BlueZHfpAdapter> logger, string deviceName = "Rotary Phone", string? adapterPath = null, BluetoothMgmtMonitor? mgmtMonitor = null)
{
    _logger = logger;
    _deviceName = deviceName;
    _adapterPath = adapterPath;
    _mgmtMonitor = mgmtMonitor;
    // ...
}
```

- [ ] **Step 6: Remove "passive mode" comments**

In `BlueZHfpAdapter.cs`, update the class doc comment:

```csharp
/// <summary>
/// Bluetooth HFP adapter for Linux using BlueZ.
/// Launches bt_manager.py to manage the BT adapter, HFP connections,
/// and call state detection via RFCOMM AT commands.
/// </summary>
```

Remove "Radio.API owns the adapter" comments from `InitializeAsync()`.

- [ ] **Step 7: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --verbosity quiet`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add scripts/bt_manager.py src/RotaryPhoneController.Core/Audio/BlueZHfpAdapter.cs
git commit -m "feat: rename hfp_monitor → bt_manager, add --adapter flag, remove passive mode"
```

---

### Task 6: RTest Changes — Configure hci0 adapter isolation

> **This task is for the RTest repo (d:/prj/rtest).** Pass this to the parallel Claude session.

**RTest Prompt:**

> RotaryPhone is taking ownership of hci1 (built-in Bluetooth adapter) for HFP phone connections. RTest must be configured to use only hci0 (USB TP-Link UB500 dongle) for A2DP radio streaming. This prevents the two projects from conflicting on the same adapter.
>
> Changes needed:
>
> 1. **BlueZ adapter path configuration:** Wherever RTest's BluetoothService or A2DP code interacts with BlueZ D-Bus, it should specify the adapter path `/org/bluez/hci0` instead of using the default adapter. Look for calls to `org.bluez.Adapter1` or `bluetoothctl` that don't specify an adapter.
>
> 2. **Configuration:** Add a `BluetoothAdapter` config option (default: `"hci0"`) to RTest's app configuration so the adapter can be changed without code changes.
>
> 3. **Discovery filtering:** If RTest uses `bluetoothctl` for scanning, pass `--adapter hci0` flag. If it uses D-Bus directly, filter to `/org/bluez/hci0/dev_*` paths.
>
> 4. **SignalR hub URL:** Verify RTest connects to RotaryPhone's hub at `http://radio:5004/hub` (not localhost:5555). If the URL is wrong, fix it in RTest's configuration.
>
> This is a Phase 2 change. RotaryPhone owns hci1, RTest owns hci0. Both projects can run simultaneously without Bluetooth conflicts.

- [ ] **Step 1: Provide the prompt above to the RTest Claude session**
- [ ] **Step 2: Verify RTest changes compile and RTest's tests pass**
- [ ] **Step 3: Deploy both projects and verify no adapter conflicts**

---

## Chunk 3: Phase 3 — Multi-Phone HFP

### Task 7: Create BluetoothDevice and PairingRequest records

**Files:**
- Create: `src/RotaryPhoneController.Core/Audio/BluetoothDevice.cs`

- [ ] **Step 1: Create the file**

```csharp
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
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Core/Audio/BluetoothDevice.cs
git commit -m "feat: add BluetoothDevice and PairingRequest record types"
```

---

### Task 8: Create IBluetoothDeviceManager interface

**Files:**
- Create: `src/RotaryPhoneController.Core/Audio/IBluetoothDeviceManager.cs`

- [ ] **Step 1: Create the interface**

```csharp
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
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Core/Audio/IBluetoothDeviceManager.cs
git commit -m "feat: add IBluetoothDeviceManager interface for multi-device BT"
```

---

### Task 9: Create MockBluetoothDeviceManager

**Files:**
- Create: `src/RotaryPhoneController.Core/Audio/MockBluetoothDeviceManager.cs`

- [ ] **Step 1: Create mock implementation**

Follow the pattern in `MockBluetoothHfpAdapter.cs`. Implement all interface members as no-ops with logging. This is used on Windows and when `UseActualBluetoothHfp == false`.

```csharp
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

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("MockBluetoothDeviceManager initialized (no real BT)");
        return Task.CompletedTask;
    }

    #pragma warning disable CS0067 // Events never used in mock
    #pragma warning restore CS0067

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
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Core/Audio/MockBluetoothDeviceManager.cs
git commit -m "feat: add MockBluetoothDeviceManager for Windows dev and testing"
```

---

### Task 10: Create BlueZBtManager (.NET subprocess wrapper)

**Files:**
- Create: `src/RotaryPhoneController.Core/Audio/BlueZBtManager.cs`
- Create: `src/RotaryPhoneController.Tests/BlueZBtManagerTests.cs`

This is the core .NET component. It launches bt_manager.py, reads JSON events from stdout, sends JSON commands via stdin, and maintains device state.

- [ ] **Step 1: Write tests for event processing**

```csharp
using Moq;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Tests;

public class BlueZBtManagerTests
{
    [Fact]
    public void ProcessEvent_Ring_FiresOnIncomingCall()
    {
        var manager = CreateManager();
        string? receivedNumber = null;
        BluetoothDevice? receivedDevice = null;
        manager.OnIncomingCall += (dev, num) => { receivedDevice = dev; receivedNumber = num; };

        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"+15551234567"}""");

        Assert.NotNull(receivedDevice);
        Assert.Equal("D4:3A:2C:64:87:9E", receivedDevice!.Address);
        Assert.Equal("+15551234567", receivedNumber);
        Assert.True(receivedDevice.HasIncomingCall);
    }

    [Fact]
    public void ProcessEvent_CallActive_WithoutATA_FiresCallAnsweredOnPhone()
    {
        var manager = CreateManager();
        BluetoothDevice? answeredDevice = null;
        manager.OnCallAnsweredOnPhone += dev => answeredDevice = dev;

        // Simulate ring then call_active without us sending ATA
        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"Unknown"}""");
        manager.ProcessEventForTest("""{"event":"call_active","address":"D4:3A:2C:64:87:9E","answered_locally":true}""");

        Assert.NotNull(answeredDevice);
        Assert.Equal("D4:3A:2C:64:87:9E", answeredDevice!.Address);
    }

    [Fact]
    public void ProcessEvent_CallActive_AfterATA_DoesNotFireCallAnsweredOnPhone()
    {
        var manager = CreateManager();
        bool answeredOnPhone = false;
        manager.OnCallAnsweredOnPhone += _ => answeredOnPhone = true;

        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"Unknown"}""");
        // Simulate that we sent ATA (by calling AnswerCallAsync which sets the flag)
        manager.MarkAnswerSent("D4:3A:2C:64:87:9E");
        manager.ProcessEventForTest("""{"event":"call_active","address":"D4:3A:2C:64:87:9E"}""");

        Assert.False(answeredOnPhone);
    }

    [Fact]
    public void ProcessEvent_CallEnded_FiresOnCallEnded()
    {
        var manager = CreateManager();
        BluetoothDevice? endedDevice = null;
        manager.OnCallEnded += dev => endedDevice = dev;

        manager.ProcessEventForTest("""{"event":"ring","address":"D4:3A:2C:64:87:9E","number":"x"}""");
        manager.ProcessEventForTest("""{"event":"call_ended","address":"D4:3A:2C:64:87:9E"}""");

        Assert.NotNull(endedDevice);
    }

    [Fact]
    public void ProcessEvent_Connected_TracksDevice()
    {
        var manager = CreateManager();

        manager.ProcessEventForTest("""{"event":"connected","address":"D4:3A:2C:64:87:9E","name":"Pixel 8 Pro"}""");

        Assert.Single(manager.ConnectedDevices);
        Assert.Equal("Pixel 8 Pro", manager.ConnectedDevices[0].Name);
    }

    [Fact]
    public void ProcessEvent_Disconnected_RemovesFromConnected()
    {
        var manager = CreateManager();
        manager.ProcessEventForTest("""{"event":"connected","address":"D4:3A:2C:64:87:9E","name":"Pixel"}""");
        manager.ProcessEventForTest("""{"event":"disconnected","address":"D4:3A:2C:64:87:9E"}""");

        Assert.Empty(manager.ConnectedDevices);
    }

    [Fact]
    public void ProcessEvent_ScoConnected_FiresEvent()
    {
        var manager = CreateManager();
        BluetoothDevice? scoDevice = null;
        manager.OnScoAudioConnected += dev => scoDevice = dev;

        manager.ProcessEventForTest("""{"event":"connected","address":"D4:3A:2C:64:87:9E","name":"Pixel"}""");
        manager.ProcessEventForTest("""{"event":"sco_connected","address":"D4:3A:2C:64:87:9E","codec":"CVSD"}""");

        Assert.NotNull(scoDevice);
        Assert.True(scoDevice!.HasScoAudio);
    }

    private static BlueZBtManager CreateManager()
    {
        var logger = new Mock<ILogger<BlueZBtManager>>();
        var config = new AppConfiguration { BluetoothAdapter = "hci1" };
        return new BlueZBtManager(logger.Object, config);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.Tests/ --filter BlueZBtManagerTests --verbosity quiet`
Expected: FAIL (BlueZBtManager doesn't exist yet)

- [ ] **Step 3: Implement BlueZBtManager**

Note: BlueZBtManager is NOT wrapped in `#if !WINDOWS`. The event processing and device state tracking is pure platform-agnostic logic. Only `RunProcessLoopAsync` launches a subprocess (which won't exist on Windows, but that's fine — it just won't start). This allows tests to run on Windows.

```csharp
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
```

- [ ] **Step 4: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --filter BlueZBtManagerTests --verbosity quiet`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Core/Audio/BlueZBtManager.cs src/RotaryPhoneController.Tests/BlueZBtManagerTests.cs
git commit -m "feat: add BlueZBtManager — subprocess wrapper for bt_manager.py with event processing"
```

---

### Task 11: Wire IBluetoothDeviceManager into DI and CallManager

**Files:**
- Modify: `src/RotaryPhoneController.Server/Program.cs:122-132`
- Modify: `src/RotaryPhoneController.Core/CallManager.cs:10-20, 53-70`
- Modify: `src/RotaryPhoneController.Core/PhoneManagerService.cs`

- [ ] **Step 1: Register IBluetoothDeviceManager in Program.cs**

Add alongside the existing IBluetoothHfpAdapter registration (keep both during transition):

```csharp
// After the existing IBluetoothHfpAdapter registration:
builder.Services.AddSingleton<IBluetoothDeviceManager>(sp =>
{
    var config = sp.GetRequiredService<AppConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    if (!config.UseActualBluetoothHfp)
        return new MockBluetoothDeviceManager(loggerFactory.CreateLogger<MockBluetoothDeviceManager>());

#if !WINDOWS
    return new BlueZBtManager(loggerFactory.CreateLogger<BlueZBtManager>(), config);
#else
    return new MockBluetoothDeviceManager(loggerFactory.CreateLogger<MockBluetoothDeviceManager>());
#endif
});
```

- [ ] **Step 2: Add IBluetoothDeviceManager to CallManager constructor**

Add `IBluetoothDeviceManager? deviceManager = null` optional parameter to CallManager constructor. Store as field. Subscribe to its events for multi-device call handling. Keep the existing `IBluetoothHfpAdapter` wiring for backward compatibility during transition.

```csharp
private readonly IBluetoothDeviceManager? _deviceManager;
private string? _activeDeviceAddress; // which BT device is handling the current call

public CallManager(
    ISipAdapter sipAdapter,
    IBluetoothHfpAdapter bluetoothAdapter,
    IRtpAudioBridge rtpBridge,
    ILogger<CallManager> logger,
    RotaryPhoneConfig phoneConfig,
    int rtpPort = 49000,
    ICallHistoryService? callHistoryService = null,
    IBluetoothDeviceManager? deviceManager = null)
{
    // ... existing assignments ...
    _deviceManager = deviceManager;

    if (_deviceManager != null)
    {
        _deviceManager.OnIncomingCall += HandleDeviceIncomingCall;
        _deviceManager.OnCallAnsweredOnPhone += HandleDeviceCallAnsweredOnPhone;
        _deviceManager.OnCallActive += HandleDeviceCallActive;
        _deviceManager.OnCallEnded += HandleDeviceCallEnded;
    }
}
```

- [ ] **Step 3: Add device-aware call handlers to CallManager**

```csharp
private void HandleDeviceIncomingCall(BluetoothDevice device, string number)
{
    _activeDeviceAddress = device.Address;
    HandleBluetoothIncomingCall(number);
}

private void HandleDeviceCallAnsweredOnPhone(BluetoothDevice device)
{
    if (_activeDeviceAddress == device.Address)
        HandleCallAnsweredOnCellPhone();
}

private void HandleDeviceCallActive(BluetoothDevice device)
{
    if (_activeDeviceAddress == device.Address && CurrentState == CallState.Dialing)
    {
        // Outgoing call connected — transition to InCall
        CurrentState = CallState.InCall;
        _logger.LogInformation("Outgoing call connected on {Device}", device.Address);
    }
}

private void HandleDeviceCallEnded(BluetoothDevice device)
{
    if (_activeDeviceAddress == device.Address)
    {
        HandleBluetoothCallEnded();
        _activeDeviceAddress = null;
    }
}
```

- [ ] **Step 4: Update PhoneManagerService to pass IBluetoothDeviceManager**

In `PhoneManagerService.RegisterPhone()`, pass the device manager to CallManager constructor. Get it from DI.

- [ ] **Step 5: Initialize IBluetoothDeviceManager in Program.cs startup**

Add initialization after service build:

```csharp
// After app.Build():
var deviceManager = app.Services.GetRequiredService<IBluetoothDeviceManager>();
await deviceManager.InitializeAsync();
```

- [ ] **Step 6: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --verbosity quiet`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/RotaryPhoneController.Server/Program.cs src/RotaryPhoneController.Core/CallManager.cs src/RotaryPhoneController.Core/PhoneManagerService.cs
git commit -m "feat: wire IBluetoothDeviceManager into DI, CallManager, and PhoneManagerService"
```

---

### Task 12: Expand bt_manager.py for multi-device HFP

**Files:**
- Modify: `scripts/bt_manager.py`

- [ ] **Step 1: Refactor to support multiple simultaneous HfpConnections**

Change `HfpProfile` to track connections by device path in a dictionary:

```python
class HfpProfile(dbus.service.Object):
    def __init__(self, bus, path):
        super().__init__(bus, path)
        self._connections = {}  # device_path -> HfpConnection
        self._conn_threads = {}

    @dbus.service.method("org.bluez.Profile1", in_signature="oha{sv}", out_signature="")
    def NewConnection(self, device, fd, fd_properties):
        fd = fd.take()
        log(f"NewConnection: device={device}, fd={fd}")

        conn = HfpConnection(device, fd)
        self._connections[device] = conn
        t = threading.Thread(target=self._run_connection, args=(device, conn), daemon=True)
        self._conn_threads[device] = t
        t.start()

    def _run_connection(self, device, conn):
        try:
            conn.run()
        finally:
            self._connections.pop(device, None)
            self._conn_threads.pop(device, None)
```

- [ ] **Step 2: Add address field to all existing events**

Update `HfpConnection` to include `self.address` in all emitted events:

```python
# In _handle_unsolicited, update ring event:
emit({"event": "ring", "address": self.address, "number": number})

# call_active — track whether WE sent ATA
emit({"event": "call_active", "address": self.address, "answered_locally": not self._we_sent_ata})

# call_ended:
emit({"event": "call_ended", "address": self.address})
```

Add `_we_sent_ata` flag to HfpConnection, set True in `answer()`, cleared on call end.

- [ ] **Step 3: Route stdin commands to correct connection by address**

Update `handle_stdin_command` to look up connection by address:

```python
def handle_stdin_command(self, cmd_dict):
    command = cmd_dict.get("command")

    # Commands that target a specific device
    if command in ("answer", "hangup", "dial"):
        # Find the connection with an active/incoming call
        conn = self._find_active_connection()
        if not conn:
            emit({"event": "error", "message": "No active RFCOMM connection"})
            return
        if command == "answer":
            conn.answer()
        elif command == "hangup":
            conn.hangup()
        elif command == "dial":
            conn.dial(cmd_dict.get("number", ""))
    # ... handle discovery/pairing commands
```

- [ ] **Step 4: Test with two paired devices**

Deploy and verify both phones connect and events include correct addresses.

- [ ] **Step 5: Commit**

```bash
git add scripts/bt_manager.py
git commit -m "feat: bt_manager multi-device HFP — track N connections, address on all events"
```

---

### Task 13: Update SignalRNotifierService for device events

**Files:**
- Modify: `src/RotaryPhoneController.Server/Services/SignalRNotifierService.cs`

- [ ] **Step 1: Subscribe to IBluetoothDeviceManager events**

In `StartAsync`, get `IBluetoothDeviceManager` from DI and subscribe:

```csharp
var deviceManager = _serviceProvider.GetService<IBluetoothDeviceManager>();
if (deviceManager != null)
{
    deviceManager.OnDeviceConnected += dev =>
        _hubContext.Clients.All.SendAsync("DeviceConnected", dev.Address, dev.Name);
    deviceManager.OnDeviceDisconnected += dev =>
        _hubContext.Clients.All.SendAsync("DeviceDisconnected", dev.Address);
    deviceManager.OnDeviceDiscovered += dev =>
        _hubContext.Clients.All.SendAsync("DeviceDiscovered", dev.Address, dev.Name);
    deviceManager.OnDevicePaired += dev =>
        _hubContext.Clients.All.SendAsync("DevicePaired", dev.Address, dev.Name);
    deviceManager.OnDeviceRemoved += dev =>
        _hubContext.Clients.All.SendAsync("DeviceRemoved", dev.Address);
    deviceManager.OnPairingRequest += req =>
        _hubContext.Clients.All.SendAsync("PairingRequest", req.Address, req.Type, req.Passkey);
}
```

- [ ] **Step 2: Run tests and commit**

```bash
git add src/RotaryPhoneController.Server/Services/SignalRNotifierService.cs
git commit -m "feat: broadcast BT device events via SignalR"
```

---

## Chunk 4: Phase 4 — Pairing UI

### Task 14: Add BlueZ Agent1 to bt_manager.py

**Files:**
- Modify: `scripts/bt_manager.py`

- [ ] **Step 1: Implement Agent1 D-Bus interface**

```python
AGENT_PATH = "/org/rotaryphone/agent"

class RotaryPhoneAgent(dbus.service.Object):
    """BlueZ Agent1 for handling pairing requests."""

    @dbus.service.method("org.bluez.Agent1", in_signature="os", out_signature="")
    def AuthorizeService(self, device, uuid):
        log(f"AuthorizeService: {device} uuid={uuid}")

    @dbus.service.method("org.bluez.Agent1", in_signature="o", out_signature="s")
    def RequestPinCode(self, device):
        log(f"RequestPinCode: {device}")
        emit({"event": "pairing_request", "address": HfpConnection._extract_address(device),
              "type": "pin", "passkey": None})
        return ""  # Will be overridden by confirm_pairing command

    @dbus.service.method("org.bluez.Agent1", in_signature="ou", out_signature="")
    def RequestConfirmation(self, device, passkey):
        addr = HfpConnection._extract_address(device)
        log(f"RequestConfirmation: {device} passkey={passkey}")
        emit({"event": "pairing_request", "address": addr,
              "type": "confirmation", "passkey": str(passkey)})
        # Block until confirmed via stdin command
        # (use threading.Event, set by confirm_pairing command)

    @dbus.service.method("org.bluez.Agent1", in_signature="", out_signature="")
    def Release(self):
        log("Agent released")

    @dbus.service.method("org.bluez.Agent1", in_signature="", out_signature="")
    def Cancel(self):
        log("Agent cancelled")
```

- [ ] **Step 2: Register agent in main()**

```python
agent = RotaryPhoneAgent(bus, AGENT_PATH)
agent_manager = dbus.Interface(
    bus.get_object("org.bluez", "/org/bluez"),
    "org.bluez.AgentManager1"
)
agent_manager.RegisterAgent(AGENT_PATH, "DisplayYesNo")
agent_manager.RequestDefaultAgent(AGENT_PATH)
log("Registered BlueZ agent")
```

- [ ] **Step 3: Add discovery commands**

```python
# In stdin command handling:
elif command == "start_discovery":
    adapter_iface = dbus.Interface(
        bus.get_object("org.bluez", adapter_path),
        "org.bluez.Adapter1"
    )
    adapter_iface.StartDiscovery()
    log("Discovery started")
elif command == "stop_discovery":
    adapter_iface = dbus.Interface(
        bus.get_object("org.bluez", adapter_path),
        "org.bluez.Adapter1"
    )
    adapter_iface.StopDiscovery()
    log("Discovery stopped")
```

- [ ] **Step 4: Add pair/remove/connect/disconnect commands**

```python
elif command == "pair":
    dev = dbus.Interface(bus.get_object("org.bluez", device_path_for(addr)), "org.bluez.Device1")
    dev.Pair()
elif command == "remove_device":
    adapter_iface.RemoveDevice(device_path_for(addr))
elif command == "connect":
    dev = dbus.Interface(bus.get_object("org.bluez", device_path_for(addr)), "org.bluez.Device1")
    dev.Connect()
elif command == "disconnect":
    dev = dbus.Interface(bus.get_object("org.bluez", device_path_for(addr)), "org.bluez.Device1")
    dev.Disconnect()
```

- [ ] **Step 5: Commit**

```bash
git add scripts/bt_manager.py
git commit -m "feat: add BlueZ Agent1, discovery, pair/remove/connect commands to bt_manager"
```

---

### Task 15: Create BluetoothController API

**Files:**
- Create: `src/RotaryPhoneController.Server/Controllers/BluetoothController.cs`

- [ ] **Step 1: Create the controller**

```csharp
using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core.Audio;

namespace RotaryPhoneController.Server.Controllers;

[ApiController]
[Route("api/bluetooth")]
public class BluetoothController : ControllerBase
{
    private readonly IBluetoothDeviceManager _deviceManager;

    public BluetoothController(IBluetoothDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        return Ok(new
        {
            paired = _deviceManager.PairedDevices,
            connected = _deviceManager.ConnectedDevices,
            adapterReady = _deviceManager.IsAdapterReady,
            adapterAddress = _deviceManager.AdapterAddress
        });
    }

    [HttpPost("discovery/start")]
    public async Task<IActionResult> StartDiscovery()
    {
        await _deviceManager.StartDiscoveryAsync();
        return Ok();
    }

    [HttpPost("discovery/stop")]
    public async Task<IActionResult> StopDiscovery()
    {
        await _deviceManager.StopDiscoveryAsync();
        return Ok();
    }

    [HttpPost("pair")]
    public async Task<IActionResult> PairDevice([FromBody] DeviceAddressRequest request)
    {
        var result = await _deviceManager.PairDeviceAsync(request.Address);
        return result ? Ok() : BadRequest("Pairing failed");
    }

    [HttpDelete("devices/{address}")]
    public async Task<IActionResult> RemoveDevice(string address)
    {
        var result = await _deviceManager.RemoveDeviceAsync(address);
        return result ? Ok() : BadRequest("Remove failed");
    }

    [HttpPost("pairing/confirm")]
    public async Task<IActionResult> ConfirmPairing([FromBody] PairingConfirmRequest request)
    {
        var result = await _deviceManager.ConfirmPairingAsync(request.Address, request.Accept);
        return result ? Ok() : BadRequest("Confirmation failed");
    }

    [HttpPut("adapter")]
    public async Task<IActionResult> SetAdapter([FromBody] AdapterConfigRequest request)
    {
        var result = await _deviceManager.SetAdapterAsync(request.Alias, request.Discoverable);
        return result ? Ok() : BadRequest("Adapter config failed");
    }

    [HttpPost("devices/{address}/connect")]
    public async Task<IActionResult> ConnectDevice(string address)
    {
        var result = await _deviceManager.ConnectDeviceAsync(address);
        return result ? Ok() : BadRequest("Connect failed");
    }

    [HttpPost("devices/{address}/disconnect")]
    public async Task<IActionResult> DisconnectDevice(string address)
    {
        var result = await _deviceManager.DisconnectDeviceAsync(address);
        return result ? Ok() : BadRequest("Disconnect failed");
    }
}

public record DeviceAddressRequest(string Address);
public record PairingConfirmRequest(string Address, bool Accept);
public record AdapterConfigRequest(string? Alias, bool? Discoverable);
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Server/Controllers/BluetoothController.cs
git commit -m "feat: add BluetoothController REST API for device management"
```

---

### Task 16: Create React Pairing page

**Files:**
- Create: `src/RotaryPhoneController.Client/src/pages/Pairing.tsx`
- Modify: `src/RotaryPhoneController.Client/src/App.tsx`
- Modify: `src/RotaryPhoneController.Client/src/components/Layout.tsx`

- [ ] **Step 1: Create Pairing.tsx**

Build a page with:
- **Paired Devices card** — list from `GET /api/bluetooth/devices`, shows name, address, connection status chip, Remove button, Connect/Disconnect button
- **Discovered Devices card** — populated via SignalR `DeviceDiscovered` events, Pair button per device
- **Scan button** — starts/stops discovery
- **Pairing dialog** — shown on `PairingRequest` SignalR event, displays passkey, Accept/Reject buttons

Use MUI components consistent with existing pages (Dashboard, Contacts, CallHistory): `Card`, `CardContent`, `List`, `ListItem`, `Chip`, `Button`, `Dialog`, `Typography`.

Subscribe to SignalR events: `DeviceDiscovered`, `DevicePaired`, `DeviceRemoved`, `DeviceConnected`, `DeviceDisconnected`, `PairingRequest`.

- [ ] **Step 2: Add route in App.tsx**

```tsx
import Pairing from './pages/Pairing';
// In Routes:
<Route path="/pairing" element={<Pairing />} />
```

- [ ] **Step 3: Add nav link in Layout.tsx**

Add a "Bluetooth" or "Devices" entry to the drawer nav list, with a Bluetooth icon.

- [ ] **Step 4: Test in browser**

Run: `cd src/RotaryPhoneController.Client && npm run dev`
Navigate to `/pairing`. Verify the page loads and API calls succeed (empty lists on Windows mock).

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Client/src/pages/Pairing.tsx src/RotaryPhoneController.Client/src/App.tsx src/RotaryPhoneController.Client/src/components/Layout.tsx
git commit -m "feat: add Bluetooth pairing page with device discovery, pairing, and management"
```

---

### Task 17: Deploy and test Phase 3-4

- [ ] **Step 1: Deploy to radio**

Run: `pwsh deploy/Deploy-ToLinux.ps1 -Logs`

- [ ] **Step 2: Verify adapter ownership**

```bash
ssh mmack@radio "bluetoothctl show /org/bluez/hci1 | head -5"
```
Expected: Shows adapter with alias "Rotary Phone"

- [ ] **Step 3: Test pairing page**

Open `http://radio:5004/pairing` in browser. Start scan. Verify discovered devices appear.

- [ ] **Step 4: Test incoming call detection (multi-device)**

Call the Pixel 8 Pro. Verify ring event includes address field in logs.

- [ ] **Step 5: Commit**

```bash
git commit -m "feat: Phase 3-4 verified — multi-device HFP + pairing UI"
```

---

## Chunk 5: Phase 5 — SCO Audio Bridge

### Task 18: Add SCO socket handling to bt_manager.py

**Files:**
- Modify: `scripts/bt_manager.py`

- [ ] **Step 1: Add SCO socket module**

```python
import socket
import struct

# Bluetooth SCO socket constants
BTPROTO_SCO = 2
SOL_SCO = 17
SCO_OPTIONS = 1

class ScoAudioBridge:
    """Bridges SCO audio to/from a local UDP port."""

    def __init__(self, device_address, udp_send_port=49100, udp_recv_port=49101):
        self.device_address = device_address
        self.udp_send_port = udp_send_port
        self.udp_recv_port = udp_recv_port
        self.sco_sock = None
        self.udp_sock = None
        self.running = False
        self._threads = []

    def start(self, sco_fd):
        """Start bridging audio between SCO file descriptor and UDP."""
        self.running = True
        self.sco_sock = socket.fromfd(sco_fd, socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, BTPROTO_SCO)

        self.udp_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp_sock.bind(("127.0.0.1", self.udp_recv_port))
        self.udp_sock.settimeout(0.1)

        # SCO → UDP (phone voice to .NET)
        t1 = threading.Thread(target=self._sco_to_udp, daemon=True)
        t1.start()
        self._threads.append(t1)

        # UDP → SCO (.NET voice to phone)
        t2 = threading.Thread(target=self._udp_to_sco, daemon=True)
        t2.start()
        self._threads.append(t2)

        emit({"event": "sco_connected", "address": self.device_address, "codec": "CVSD"})
        log(f"SCO audio bridge started for {self.device_address}")

    def stop(self):
        self.running = False
        try:
            if self.sco_sock: self.sco_sock.close()
        except: pass
        try:
            if self.udp_sock: self.udp_sock.close()
        except: pass
        emit({"event": "sco_disconnected", "address": self.device_address})
        log(f"SCO audio bridge stopped for {self.device_address}")

    def _sco_to_udp(self):
        """Read PCM from SCO, forward to .NET via UDP."""
        while self.running:
            try:
                data = self.sco_sock.recv(480)  # 48 bytes typical SCO packet (CVSD)
                if data:
                    self.udp_sock.sendto(data, ("127.0.0.1", self.udp_send_port))
            except (OSError, IOError):
                break
        log("SCO→UDP thread ended")

    def _udp_to_sco(self):
        """Read PCM from .NET via UDP, write to SCO."""
        while self.running:
            try:
                data, _ = self.udp_sock.recvfrom(480)
                if data:
                    self.sco_sock.send(data)
            except socket.timeout:
                continue
            except (OSError, IOError):
                break
        log("UDP→SCO thread ended")
```

- [ ] **Step 2: Accept SCO connections in HfpConnection**

After answering a call (when `_we_sent_ata` is True), accept SCO:

```python
def _on_call_active(self):
    """Called when call becomes active. Start SCO if we answered."""
    if self._we_sent_ata:
        # We answered — accept SCO connection
        threading.Thread(target=self._accept_sco, daemon=True).start()

def _accept_sco(self):
    """Listen for and accept an incoming SCO connection from the AG."""
    try:
        sco_listen = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, BTPROTO_SCO)
        sco_listen.bind(bytes(6))  # Bind to any local BT address
        sco_listen.listen(1)
        sco_listen.settimeout(10.0)  # AG should open SCO within seconds
        conn, addr = sco_listen.accept()
        sco_listen.close()

        self._sco_bridge = ScoAudioBridge(self.address)
        self._sco_bridge.start(conn.fileno())
    except Exception as e:
        log(f"SCO accept failed: {e}")
        emit({"event": "error", "message": f"SCO connection failed: {e}"})
```

- [ ] **Step 3: Clean up SCO on call end**

In `HfpConnection`, when call ends, stop the SCO bridge:

```python
# In _handle_ciev, when call=0:
if hasattr(self, '_sco_bridge') and self._sco_bridge:
    self._sco_bridge.stop()
    self._sco_bridge = None
```

- [ ] **Step 4: Commit**

```bash
git add scripts/bt_manager.py
git commit -m "feat: add SCO socket handling and audio bridge in bt_manager.py"
```

---

### Task 19: Create ScoRtpBridge in .NET

**Files:**
- Create: `src/RotaryPhoneController.Core/Audio/ScoRtpBridge.cs`
- Create: `src/RotaryPhoneController.Tests/ScoRtpBridgeTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using RotaryPhoneController.Core.Audio;
using Moq;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Tests;

public class ScoRtpBridgeTests
{
    [Fact]
    public void G711Encode_Decode_RoundTrip()
    {
        // Verify G711Codec works for our audio path
        var pcm = new byte[320]; // 20ms at 8kHz 16-bit mono
        for (int i = 0; i < pcm.Length; i += 2)
        {
            // Simple sine wave
            short sample = (short)(Math.Sin(i * 0.1) * 16000);
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)(sample >> 8);
        }

        var encoded = G711Codec.EncodeMuLaw(pcm, pcm.Length);
        Assert.Equal(160, encoded.Length); // Half size after encoding

        var decoded = G711Codec.DecodeMuLaw(encoded);
        Assert.Equal(320, decoded.Length); // Back to original size
    }
}
```

- [ ] **Step 2: Implement ScoRtpBridge**

Note: `ScoRtpBridge` must match the existing `IRtpAudioBridge` signatures — `StartBridgeAsync(string, AudioRoute)` returning `Task<bool>`, etc. The string endpoint is parsed to `IPEndPoint` internally.

```csharp
#if !WINDOWS
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Bridges SCO audio (via local UDP from bt_manager.py) to RTP (to/from HT801).
/// Replaces PipeWireRtpAudioBridge for production BT call audio.
///
/// Audio path:
///   SCO → bt_manager.py → UDP:scoRecvPort → this bridge → G.711 encode → RTP → HT801
///   HT801 → RTP → this bridge → G.711 decode → UDP:scoSendPort → bt_manager.py → SCO
/// </summary>
public class ScoRtpBridge : IRtpAudioBridge
{
    private readonly ILogger<ScoRtpBridge> _logger;
    private readonly int _scoRecvPort;  // UDP port to receive PCM from bt_manager.py
    private readonly int _scoSendPort;  // UDP port to send PCM to bt_manager.py
    private UdpClient? _scoRecvClient;
    private UdpClient? _scoSendClient;
    private UdpClient? _rtpClient;
    private CancellationTokenSource? _cts;
    private IPEndPoint? _rtpRemoteEndpoint;

    public bool IsActive { get; private set; }
    public AudioRoute CurrentRoute { get; private set; }

    public event Action? OnBridgeEstablished;
    public event Action? OnBridgeTerminated;
    public event Action<string>? OnBridgeError;

    public ScoRtpBridge(ILogger<ScoRtpBridge> logger, int scoRecvPort = 49100, int scoSendPort = 49101)
    {
        _logger = logger;
        _scoRecvPort = scoRecvPort;
        _scoSendPort = scoSendPort;
    }

    public async Task<bool> StartBridgeAsync(string rtpEndpoint, AudioRoute route)
    {
        if (IsActive) return false;

        // Parse "ip:port" string to IPEndPoint (matches IRtpAudioBridge signature)
        var parts = rtpEndpoint.Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var port))
        {
            _logger.LogError("Invalid RTP endpoint format: {Endpoint}", rtpEndpoint);
            return false;
        }

        _rtpRemoteEndpoint = new IPEndPoint(ip, port);
        CurrentRoute = route;
        _cts = new CancellationTokenSource();

        // UDP for SCO PCM data from/to bt_manager.py
        _scoRecvClient = new UdpClient(_scoRecvPort);
        _scoSendClient = new UdpClient();

        // UDP for RTP to/from HT801
        _rtpClient = new UdpClient(0); // Ephemeral port for RTP

        IsActive = true;
        OnBridgeEstablished?.Invoke();
        _logger.LogInformation("SCO-RTP bridge started: SCO recv={ScoRecv} send={ScoSend} RTP={Rtp}",
            _scoRecvPort, _scoSendPort, _rtpRemoteEndpoint);

        // Start bidirectional bridge
        var ct = _cts.Token;
        _ = Task.Run(() => ScoToRtpLoop(ct), ct);
        _ = Task.Run(() => RtpToScoLoop(ct), ct);

        return true;
    }

    private async Task ScoToRtpLoop(CancellationToken ct)
    {
        // Read PCM from SCO (via UDP), encode G.711, send as RTP to HT801
        try
        {
            while (!ct.IsCancellationRequested && IsActive)
            {
                var result = await _scoRecvClient!.ReceiveAsync(ct);
                var pcm = result.Buffer;
                if (pcm.Length == 0) continue;

                var encoded = G711Codec.EncodeMuLaw(pcm, pcm.Length);
                // TODO: wrap in proper RTP packet (header + payload)
                // For now, send raw G.711 — will need RTP framing
                await _rtpClient!.SendAsync(encoded, encoded.Length, _rtpRemoteEndpoint!);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SCO→RTP loop error");
            OnBridgeError?.Invoke(ex.Message);
        }
    }

    private async Task RtpToScoLoop(CancellationToken ct)
    {
        // Receive RTP from HT801, decode G.711, send PCM to SCO via UDP
        var scoTarget = new IPEndPoint(IPAddress.Loopback, _scoSendPort);
        try
        {
            while (!ct.IsCancellationRequested && IsActive)
            {
                var result = await _rtpClient!.ReceiveAsync(ct);
                var rtpData = result.Buffer;
                if (rtpData.Length == 0) continue;

                // TODO: strip RTP header (12 bytes) to get G.711 payload
                // For now, treat as raw G.711
                var decoded = G711Codec.DecodeMuLaw(rtpData);
                await _scoSendClient!.SendAsync(decoded, decoded.Length, scoTarget);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTP→SCO loop error");
            OnBridgeError?.Invoke(ex.Message);
        }
    }

    public Task<bool> StopBridgeAsync()
    {
        if (!IsActive) return Task.FromResult(false);

        _cts?.Cancel();
        _scoRecvClient?.Close();
        _scoSendClient?.Close();
        _rtpClient?.Close();
        IsActive = false;
        OnBridgeTerminated?.Invoke();
        _logger.LogInformation("SCO-RTP bridge stopped");
        return Task.FromResult(true);
    }

    public Task<bool> ChangeAudioRouteAsync(AudioRoute newRoute)
    {
        CurrentRoute = newRoute;
        return Task.FromResult(true);
    }
}
#endif
```

- [ ] **Step 3: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --filter ScoRtpBridgeTests --verbosity quiet`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Core/Audio/ScoRtpBridge.cs src/RotaryPhoneController.Tests/ScoRtpBridgeTests.cs
git commit -m "feat: add ScoRtpBridge — bridges SCO audio (via UDP) to RTP for HT801"
```

---

### Task 20: Add CancelPendingInviteAsync to ISipAdapter

**Files:**
- Modify: `src/RotaryPhoneController.Core/ISipAdapter.cs`
- Modify: `src/RotaryPhoneController.Core/SIPSorceryAdapter.cs`

- [ ] **Step 1: Add method to interface**

```csharp
// In ISipAdapter.cs, add:
/// <summary>
/// Cancel a pending SIP INVITE (stop the rotary phone from ringing).
/// Sends SIP CANCEL for an unanswered INVITE dialog.
/// </summary>
void CancelPendingInvite();
```

- [ ] **Step 2: Implement in SIPSorceryAdapter**

Track the pending INVITE's SIP dialog so we can send BYE to stop ringing. The simplest approach: store the `SIPRequest` from `SendInviteToHT801` and send a BYE with matching Call-ID when canceling.

```csharp
private SIPRequest? _pendingInviteRequest;

// In SendInviteToHT801, after constructing the INVITE request:
_pendingInviteRequest = inviteRequest;

public void CancelPendingInvite()
{
    var pending = _pendingInviteRequest;
    _pendingInviteRequest = null;

    if (pending == null)
    {
        _logger.LogDebug("No pending INVITE to cancel");
        return;
    }

    try
    {
        _logger.LogInformation("Canceling pending SIP INVITE (Call-ID: {CallId})", pending.Header.CallId);

        // Send BYE to the HT801 to stop ringing
        var byeRequest = SIPRequest.GetRequest(SIPMethodsEnum.BYE, pending.URI);
        byeRequest.Header.CallId = pending.Header.CallId;
        byeRequest.Header.From = pending.Header.From;
        byeRequest.Header.To = pending.Header.To;
        byeRequest.Header.CSeq = pending.Header.CSeq + 1;
        byeRequest.Header.CSeqMethod = SIPMethodsEnum.BYE;

        _sipTransport?.SendRequestAsync(byeRequest);
        _logger.LogInformation("SIP BYE sent to cancel ringing");
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error canceling pending INVITE");
    }
}
```

- [ ] **Step 3: Call CancelPendingInvite in CallManager.HandleCallAnsweredOnCellPhone**

```csharp
// In HandleCallAnsweredOnCellPhone:
_sipAdapter.CancelPendingInvite(); // Stop rotary phone ringing
```

- [ ] **Step 4: Run tests and commit**

```bash
git add src/RotaryPhoneController.Core/ISipAdapter.cs src/RotaryPhoneController.Core/SIPSorceryAdapter.cs src/RotaryPhoneController.Core/CallManager.cs
git commit -m "feat: add CancelPendingInvite to ISipAdapter — stops ringing on cell-phone answer"
```

---

### Task 21: Defer CallManager.StartCall InCall transition

**Files:**
- Modify: `src/RotaryPhoneController.Core/CallManager.cs:288-315`

- [ ] **Step 1: Change StartCall to stay in Dialing**

Currently `StartCall()` transitions directly to `InCall` (line 312). Change it to send the dial command but keep state as `Dialing`. Transition to `InCall` happens when `call_active` event arrives (via `HandleDeviceCallActive` or existing `HandleBluetoothIncomingCall` path).

```csharp
public async Task StartCall(string number)
{
    if (CurrentState != CallState.Dialing) return;

    _logger.LogInformation("Initiating outgoing call to {Number}", number);

    if (_deviceManager != null)
    {
        // Select first available connected device
        var device = _deviceManager.ConnectedDevices
            .FirstOrDefault(d => !d.HasActiveCall);
        if (device == null)
        {
            _logger.LogWarning("No available BT devices for outgoing call");
            CurrentState = CallState.Idle;
            return;
        }

        _activeDeviceAddress = device.Address;
        var success = await _deviceManager.DialAsync(device.Address, number);
        if (!success)
        {
            _logger.LogWarning("Dial command failed");
            CurrentState = CallState.Idle;
            return;
        }

        // Stay in Dialing — transition to InCall when +CIEV: call=1 arrives
        _logger.LogInformation("Dial command sent to {Device}, waiting for call=1", device.Address);
    }
    else
    {
        // Legacy path using IBluetoothHfpAdapter
        var success = await _bluetoothAdapter.InitiateCallAsync(number);
        if (success) CurrentState = CallState.InCall;
    }
}
```

- [ ] **Step 2: Add handler for outgoing call becoming active**

The `call_active` event (without `answered_locally`) for an outgoing call should transition `Dialing → InCall`:

```csharp
// In the device manager event subscription:
// When call_active fires and we're in Dialing state, transition to InCall
private void HandleDeviceCallActive(BluetoothDevice device)
{
    if (_activeDeviceAddress == device.Address && CurrentState == CallState.Dialing)
    {
        CurrentState = CallState.InCall;
        _logger.LogInformation("Outgoing call connected on {Device}", device.Address);
    }
}
```

- [ ] **Step 3: Run tests and commit**

```bash
git add src/RotaryPhoneController.Core/CallManager.cs
git commit -m "fix: defer InCall transition until call=1 for outgoing calls"
```

---

### Task 22: Wire ScoRtpBridge to SCO events

**Files:**
- Modify: `src/RotaryPhoneController.Core/CallManager.cs`

- [ ] **Step 1: Subscribe to SCO audio events**

In the CallManager constructor, subscribe to `OnScoAudioConnected` / `OnScoAudioDisconnected` to start/stop the RTP bridge:

```csharp
if (_deviceManager != null)
{
    _deviceManager.OnScoAudioConnected += HandleScoConnected;
    _deviceManager.OnScoAudioDisconnected += HandleScoDisconnected;
}

private void HandleScoConnected(BluetoothDevice device)
{
    if (_activeDeviceAddress != device.Address) return;
    if (CurrentState != CallState.InCall && CurrentState != CallState.Ringing) return;

    // Start the RTP bridge — audio flows through rotary handset
    _logger.LogInformation("SCO audio connected for {Device}, starting RTP bridge", device.Address);
    var endpoint = $"{_phoneConfig.HT801IpAddress}:{_rtpPort}";
    _ = _rtpBridge.StartBridgeAsync(endpoint, AudioRoute.RotaryPhone);
}

private void HandleScoDisconnected(BluetoothDevice device)
{
    if (_activeDeviceAddress != device.Address) return;

    _logger.LogInformation("SCO audio disconnected for {Device}, stopping RTP bridge", device.Address);
    _ = _rtpBridge.StopBridgeAsync();
}
```

- [ ] **Step 2: Run tests and commit**

```bash
git add src/RotaryPhoneController.Core/CallManager.cs
git commit -m "feat: wire SCO audio events to RTP bridge — audio starts/stops with SCO lifecycle"
```

---

### Task 23: Deploy and test Phase 5 audio

- [ ] **Step 1: Deploy**

Run: `pwsh deploy/Deploy-ToLinux.ps1 -Logs`

- [ ] **Step 2: Test incoming call with rotary answer**

1. Call the Pixel 8 Pro
2. Rotary phone should ring
3. Lift handset — ATA sent, SCO opens, audio bridge starts
4. Speak into rotary handset — voice should be audible to caller
5. Caller speaks — voice should come through rotary earpiece

- [ ] **Step 3: Test incoming call with phone answer**

1. Call the Pixel 8 Pro
2. Answer on the phone screen
3. Verify: NO SCO opens, audio stays on phone, rotary stops ringing

- [ ] **Step 4: Test outgoing call**

1. Lift handset, dial number
2. Verify call goes through connected phone
3. Audio flows through rotary handset

- [ ] **Step 5: Commit**

```bash
git commit -m "feat: Phase 5 verified — SCO audio bridge working"
```

---

## Chunk 6: Phase 6 — Polish

### Task 24: Fast-busy tone when no phones available

**Files:**
- Modify: `src/RotaryPhoneController.Core/CallManager.cs`

- [ ] **Step 1: Check for available devices before dialing**

In `HandleHookChange(isOffHook: true)`, when state is Idle and user lifts handset, check if any BT device is connected:

```csharp
if (_deviceManager != null && _deviceManager.ConnectedDevices.Count == 0)
{
    _logger.LogWarning("No BT devices connected — cannot make calls");
    // TODO: Generate fast-busy tone via SIP or emit error state
    // For now, just log and stay idle
    return;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Core/CallManager.cs
git commit -m "feat: warn when no BT devices available for outgoing call"
```

---

### Task 25: BT reconnection logic

**Files:**
- Modify: `scripts/bt_manager.py`

- [ ] **Step 1: Add auto-reconnect on disconnect**

When a paired device disconnects, attempt reconnection after a delay:

```python
def _on_device_disconnected(self, device_path):
    addr = HfpConnection._extract_address(device_path)
    emit({"event": "disconnected", "address": addr})

    # Auto-reconnect after delay
    def try_reconnect():
        try:
            dev = dbus.Interface(bus.get_object("org.bluez", device_path), "org.bluez.Device1")
            dev.Connect()
            log(f"Reconnected to {addr}")
        except Exception as e:
            log(f"Reconnect failed for {addr}: {e}")
        return False
    GLib.timeout_add(5000, try_reconnect)
```

- [ ] **Step 2: Commit**

```bash
git add scripts/bt_manager.py
git commit -m "feat: auto-reconnect to paired devices on disconnect"
```

---

### Task 26: RTest Changes — Hub URL alignment

> **This task is for the RTest repo (d:/prj/rtest).** Pass to the parallel Claude session.

**RTest Prompt:**

> Verify and fix the SignalR hub URL that RTest uses to connect to RotaryPhone.
>
> **Correct URL:** `http://radio:5004/hub`
>
> RotaryPhone serves its SignalR hub at port 5004, path `/hub`. The hostname `radio` resolves to the Linux host running both services.
>
> 1. Find where RTest configures the RotaryPhone hub URL (likely in `SignalRService.ts`, `appsettings.json`, or a config constant).
> 2. If it says `localhost:5555/hubs/phone` or any other URL, change it to `http://radio:5004/hub`.
> 3. Make this URL configurable (environment variable or config file) so it can be changed without code changes.
> 4. Verify RTest still receives `CallStateChanged`, `IncomingCall`, and `CallerResolved` events.

- [ ] **Step 1: Provide the prompt above to the RTest Claude session**
- [ ] **Step 2: Verify RTest reconnects to the correct hub**

---

### Task 27: Final integration test and cleanup

- [ ] **Step 1: Run all tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --verbosity quiet`
Expected: All PASS

- [ ] **Step 2: Deploy full stack**

Run: `pwsh deploy/Deploy-ToLinux.ps1 -Logs`

- [ ] **Step 3: End-to-end test checklist**

1. [ ] Open `http://radio:5004/pairing` — see paired devices
2. [ ] Call Pixel 8 Pro → rotary phone rings → lift handset → talk → hang up
3. [ ] Call Pixel 8 Pro → answer on phone → audio on phone, rotary stops ringing
4. [ ] Lift handset → dial number → call goes through → talk → hang up
5. [ ] Second phone paired and connected → receives calls independently
6. [ ] Service restart → HT801 re-registers → hook detection works

- [ ] **Step 4: Use superpowers:finishing-a-development-branch to complete**

---

## RTest Changes Summary

All RTest changes are isolated to two tasks:

| Task | Phase | Description |
|------|-------|-------------|
| Task 6 | Phase 2 | Configure hci0 adapter isolation — add `BluetoothAdapter` config, filter to `/org/bluez/hci0` |
| Task 26 | Phase 6 | Hub URL alignment — fix SignalR URL to `http://radio:5004/hub` |

Both tasks include prompts ready to pass to the parallel Claude session running in the RTest repo.
