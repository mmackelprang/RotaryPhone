# GV Bridge Phase A: Core Interfaces + CallAdapter Registry

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create the unified `ICallAdapter` / `ICallAdapterRegistry` abstraction in Core, promote shared interfaces from GVTrunk, wrap existing adapters, and refactor `CallManager` to use the registry for runtime mode switching between Bluetooth, SIP Trunk, and GV Browser call paths.

**Architecture:** `ICallAdapter` is a new interface representing a remote call path (BT phone, SIP trunk, GV browser). `ICallAdapterRegistry` holds all registered adapters and switches the active one at runtime. `CallManager` swaps its direct `ISipAdapter` dependency for `ICallAdapterRegistry` and rebinds events on mode change. The existing `SIPSorceryAdapter` (HT801 local SIP) remains unchanged — it handles hook/digit events from the physical phone, which are independent of the remote call path.

**Tech Stack:** .NET 10, xUnit + Moq

**Spec:** `docs/PRD-GVBrowserBridge.md` (sections 4.2, 4.3, 5.1, 9)

**Key architectural insight:** The HT801 SIPSorceryAdapter is NOT part of the adapter registry. It always runs as the local phone interface (detecting hook changes, dialed digits, sending INVITEs to ring the phone). The `ICallAdapter` abstraction covers only the REMOTE call path — where the call goes after the rotary phone picks up. This is the critical distinction between "local phone interface" (ISipAdapter/SIPSorceryAdapter) and "remote call path" (ICallAdapter).

---

## File Structure

### New Files in Core

| File | Responsibility |
|------|---------------|
| `src/RotaryPhoneController.Core/ICallAdapter.cs` | Universal remote call path interface |
| `src/RotaryPhoneController.Core/ICallAdapterRegistry.cs` | Runtime mode switching service |
| `src/RotaryPhoneController.Core/CallAdapterMode.cs` | Enum: BluetoothHfp, SipTrunk, GVBrowser |
| `src/RotaryPhoneController.Core/CallAdapterRegistry.cs` | Registry implementation |
| `src/RotaryPhoneController.Core/Adapters/BluetoothCallAdapter.cs` | ICallAdapter wrapping BT HFP path |
| `src/RotaryPhoneController.Core/Adapters/SipTrunkCallAdapter.cs` | ICallAdapter wrapping GVTrunkAdapter |

### New Test Files

| File | Responsibility |
|------|---------------|
| `src/RotaryPhoneController.Tests/CallAdapterRegistryTests.cs` | Registry mode switching, lifecycle |

### Modified Files

| File | Changes |
|------|---------|
| `src/RotaryPhoneController.Core/CallManager.cs` | Add ICallAdapterRegistry dependency, rebind events on mode change |
| `src/RotaryPhoneController.Core/PhoneManagerService.cs` | Pass ICallAdapterRegistry to CallManager |
| `src/RotaryPhoneController.Server/Program.cs` | Register ICallAdapterRegistry, register adapters |

### Files NOT Modified

| File | Why |
|------|-----|
| `ISipAdapter.cs` | Remains as-is — local phone interface |
| `SIPSorceryAdapter.cs` | Remains as-is — HT801 hook/digit handling |
| `IBluetoothHfpAdapter.cs` | Remains as-is — wrapped by BluetoothCallAdapter |
| `IBluetoothDeviceManager.cs` | Remains as-is — wrapped by BluetoothCallAdapter |
| `GVTrunkAdapter.cs` | Remains as-is — wrapped by SipTrunkCallAdapter |

---

## Chunk 1: Core Interfaces + Enum

### Task 1: Create CallAdapterMode enum

**Files:**
- Create: `src/RotaryPhoneController.Core/CallAdapterMode.cs`

- [ ] **Step 1: Create enum**

```csharp
namespace RotaryPhoneController.Core;

public enum CallAdapterMode
{
    BluetoothHfp,
    SipTrunk,
    GVBrowser
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/CallAdapterMode.cs
git commit -m "feat: add CallAdapterMode enum — BluetoothHfp, SipTrunk, GVBrowser"
```

---

### Task 2: Create ICallAdapter interface

**Files:**
- Create: `src/RotaryPhoneController.Core/ICallAdapter.cs`

- [ ] **Step 1: Create interface**

```csharp
namespace RotaryPhoneController.Core;

/// <summary>
/// Universal adapter for a remote call path (BT phone, SIP trunk, GV browser).
/// Each implementation wraps a specific call technology. The active adapter is
/// selected at runtime via ICallAdapterRegistry.
///
/// Note: This does NOT replace ISipAdapter. ISipAdapter handles the local HT801
/// phone interface (hook changes, dialed digits, INVITE to ring). ICallAdapter
/// handles the remote side of the call — where it goes after the user picks up.
/// </summary>
public interface ICallAdapter
{
    CallAdapterMode Mode { get; }

    /// <summary>
    /// Whether this adapter is ready to handle calls.
    /// BT: device paired and connected. SIP: registered. GV: extension connected.
    /// </summary>
    bool IsAvailable { get; }

    event Action<bool>? OnAvailabilityChanged;

    /// <summary>Incoming call detected on this path. Parameter is caller number or display name.</summary>
    event Action<string>? OnIncomingCall;

    /// <summary>Call was answered (on the remote side, e.g., user answered on cell phone).</summary>
    event Action? OnCallAnswered;

    /// <summary>Call ended on the remote side.</summary>
    event Action? OnCallEnded;

    /// <summary>DTMF digit received (Phase 2 / IVR).</summary>
    event Action<string>? OnDtmfReceived;

    /// <summary>Activate this adapter (start listening, connect, register).</summary>
    Task ActivateAsync(CancellationToken ct = default);

    /// <summary>Deactivate this adapter (stop listening, disconnect, unregister).</summary>
    Task DeactivateAsync(CancellationToken ct = default);

    /// <summary>Place an outbound call. Returns a session ID.</summary>
    Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default);

    /// <summary>Answer an incoming call on this path (e.g., send ATA over BT, answer GV call).</summary>
    Task AnswerCallAsync(CancellationToken ct = default);

    /// <summary>Hang up the current call on this path.</summary>
    Task HangUpAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/ICallAdapter.cs
git commit -m "feat: add ICallAdapter interface — universal remote call path contract"
```

---

### Task 3: Create ICallAdapterRegistry interface

**Files:**
- Create: `src/RotaryPhoneController.Core/ICallAdapterRegistry.cs`

- [ ] **Step 1: Create interface**

```csharp
namespace RotaryPhoneController.Core;

/// <summary>
/// Runtime mode switcher for call adapters. Holds all registered ICallAdapter
/// implementations and routes calls through the currently active one.
/// Persists the selected mode across restarts.
/// </summary>
public interface ICallAdapterRegistry
{
    CallAdapterMode ActiveMode { get; }
    ICallAdapter ActiveAdapter { get; }
    IReadOnlyList<CallAdapterMode> AvailableModes { get; }

    event Action<CallAdapterMode>? OnModeChanged;

    /// <summary>
    /// Switch to a different call adapter. Deactivates the current adapter
    /// and activates the new one. If a call is active, hangs up first.
    /// </summary>
    Task SwitchModeAsync(CallAdapterMode mode, CancellationToken ct = default);

    /// <summary>Register an adapter. Called during DI setup.</summary>
    void Register(ICallAdapter adapter);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/ICallAdapterRegistry.cs
git commit -m "feat: add ICallAdapterRegistry interface — runtime mode switching"
```

---

## Chunk 2: CallAdapterRegistry Implementation + Tests

### Task 4: Write CallAdapterRegistry tests

**Files:**
- Create: `src/RotaryPhoneController.Tests/CallAdapterRegistryTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using Xunit;
using Moq;
using RotaryPhoneController.Core;

namespace RotaryPhoneController.Tests;

public class CallAdapterRegistryTests
{
    private Mock<ICallAdapter> CreateMockAdapter(CallAdapterMode mode, bool available = true)
    {
        var mock = new Mock<ICallAdapter>();
        mock.Setup(a => a.Mode).Returns(mode);
        mock.Setup(a => a.IsAvailable).Returns(available);
        return mock;
    }

    [Fact]
    public void Register_AddsAdapter()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        registry.Register(bt.Object);

        Assert.Single(registry.AvailableModes);
        Assert.Contains(CallAdapterMode.BluetoothHfp, registry.AvailableModes);
    }

    [Fact]
    public async Task SwitchMode_ActivatesNewAdapter()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        var sip = CreateMockAdapter(CallAdapterMode.SipTrunk);
        registry.Register(bt.Object);
        registry.Register(sip.Object);

        await registry.SwitchModeAsync(CallAdapterMode.SipTrunk);

        Assert.Equal(CallAdapterMode.SipTrunk, registry.ActiveMode);
        sip.Verify(a => a.ActivateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwitchMode_DeactivatesPreviousAdapter()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        var sip = CreateMockAdapter(CallAdapterMode.SipTrunk);
        registry.Register(bt.Object);
        registry.Register(sip.Object);

        await registry.SwitchModeAsync(CallAdapterMode.BluetoothHfp);
        await registry.SwitchModeAsync(CallAdapterMode.SipTrunk);

        bt.Verify(a => a.DeactivateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwitchMode_FiresOnModeChanged()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        registry.Register(bt.Object);

        CallAdapterMode? firedMode = null;
        registry.OnModeChanged += mode => firedMode = mode;

        await registry.SwitchModeAsync(CallAdapterMode.BluetoothHfp);

        Assert.Equal(CallAdapterMode.BluetoothHfp, firedMode);
    }

    [Fact]
    public async Task SwitchMode_ThrowsForUnregisteredMode()
    {
        var registry = new CallAdapterRegistry();
        var bt = CreateMockAdapter(CallAdapterMode.BluetoothHfp);
        registry.Register(bt.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SwitchModeAsync(CallAdapterMode.GVBrowser));
    }

    [Fact]
    public void ActiveAdapter_ThrowsWhenNoAdapterActive()
    {
        var registry = new CallAdapterRegistry();
        Assert.Throws<InvalidOperationException>(() => _ = registry.ActiveAdapter);
    }
}
```

- [ ] **Step 2: Verify tests fail (CallAdapterRegistry doesn't exist yet)**

Run: `dotnet build src/RotaryPhoneController.Tests/`
Expected: Build error — `CallAdapterRegistry` not found.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Tests/CallAdapterRegistryTests.cs
git commit -m "test: add CallAdapterRegistry tests (failing — no implementation)"
```

---

### Task 5: Implement CallAdapterRegistry

**Files:**
- Create: `src/RotaryPhoneController.Core/CallAdapterRegistry.cs`

- [ ] **Step 1: Implement**

```csharp
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core;

public class CallAdapterRegistry : ICallAdapterRegistry
{
    private readonly Dictionary<CallAdapterMode, ICallAdapter> _adapters = new();
    private ICallAdapter? _activeAdapter;
    private readonly ILogger<CallAdapterRegistry>? _logger;

    public CallAdapterMode ActiveMode { get; private set; }

    public ICallAdapter ActiveAdapter =>
        _activeAdapter ?? throw new InvalidOperationException("No adapter is active. Call SwitchModeAsync first.");

    public IReadOnlyList<CallAdapterMode> AvailableModes =>
        _adapters.Keys.ToList().AsReadOnly();

    public event Action<CallAdapterMode>? OnModeChanged;

    public CallAdapterRegistry(ILogger<CallAdapterRegistry>? logger = null)
    {
        _logger = logger;
    }

    public void Register(ICallAdapter adapter)
    {
        _adapters[adapter.Mode] = adapter;
        _logger?.LogInformation("Registered call adapter: {Mode}", adapter.Mode);
    }

    public async Task SwitchModeAsync(CallAdapterMode mode, CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(mode, out var newAdapter))
            throw new InvalidOperationException($"No adapter registered for mode: {mode}");

        if (_activeAdapter != null && _activeAdapter.Mode != mode)
        {
            _logger?.LogInformation("Deactivating adapter: {Mode}", _activeAdapter.Mode);
            await _activeAdapter.DeactivateAsync(ct);
        }

        _activeAdapter = newAdapter;
        ActiveMode = mode;

        _logger?.LogInformation("Activating adapter: {Mode}", mode);
        await newAdapter.ActivateAsync(ct);

        OnModeChanged?.Invoke(mode);
        _logger?.LogInformation("Call adapter mode switched to: {Mode}", mode);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/ --filter CallAdapterRegistryTests --verbosity quiet`
Expected: All 6 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/CallAdapterRegistry.cs
git commit -m "feat: implement CallAdapterRegistry — runtime mode switching"
```

---

## Chunk 3: Adapter Wrappers

### Task 6: Create BluetoothCallAdapter

**Files:**
- Create: `src/RotaryPhoneController.Core/Adapters/BluetoothCallAdapter.cs`

This wraps the existing BT HFP path (IBluetoothDeviceManager + IBluetoothHfpAdapter) as an ICallAdapter.

- [ ] **Step 1: Implement**

```csharp
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
        // Wire up events from BT adapters
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
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/Adapters/BluetoothCallAdapter.cs
git commit -m "feat: add BluetoothCallAdapter — wraps BT HFP path as ICallAdapter"
```

---

### Task 7: Create SipTrunkCallAdapter

**Files:**
- Create: `src/RotaryPhoneController.Core/Adapters/SipTrunkCallAdapter.cs`

This wraps the existing GVTrunkAdapter (ITrunkAdapter) as an ICallAdapter.

- [ ] **Step 1: Implement**

```csharp
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVTrunk.Interfaces;

namespace RotaryPhoneController.Core.Adapters;

/// <summary>
/// ICallAdapter wrapper for the SIP Trunk (VoIP.ms) call path.
/// Delegates to ITrunkAdapter from the GVTrunk project.
/// </summary>
public class SipTrunkCallAdapter : ICallAdapter
{
    private readonly ITrunkAdapter _trunk;
    private readonly ILogger<SipTrunkCallAdapter> _logger;

    public CallAdapterMode Mode => CallAdapterMode.SipTrunk;
    public bool IsAvailable => _trunk.IsRegistered;

    public event Action<bool>? OnAvailabilityChanged;
    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnswered;
    public event Action? OnCallEnded;
    public event Action<string>? OnDtmfReceived;

    public SipTrunkCallAdapter(ITrunkAdapter trunk, ILogger<SipTrunkCallAdapter> logger)
    {
        _trunk = trunk;
        _logger = logger;
    }

    public Task ActivateAsync(CancellationToken ct = default)
    {
        _trunk.OnRegistrationChanged += registered => OnAvailabilityChanged?.Invoke(registered);
        _trunk.OnIncomingCall += () => OnIncomingCall?.Invoke("Unknown");
        _trunk.OnHookChange += isOffHook =>
        {
            if (!isOffHook) OnCallEnded?.Invoke();
        };

        _trunk.StartListening();
        _ = _trunk.RegisterAsync(ct);
        _logger.LogInformation("SipTrunkCallAdapter activated");
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        await _trunk.UnregisterAsync(ct);
        _logger.LogInformation("SipTrunkCallAdapter deactivated");
    }

    public async Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default)
    {
        return await _trunk.PlaceOutboundCallAsync(e164Number);
    }

    public Task AnswerCallAsync(CancellationToken ct = default)
    {
        // SIP trunk answering is handled by the SIPSorceryAdapter (HT801 path)
        _logger.LogDebug("SipTrunkCallAdapter.AnswerCallAsync — delegating to HT801 SIP adapter");
        return Task.CompletedTask;
    }

    public Task HangUpAsync(CancellationToken ct = default)
    {
        _trunk.CancelPendingInvite();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded. (Core references GVTrunk — verify this reference exists or add it.)

Note: Core needs to reference GVTrunk for ITrunkAdapter. If this creates a circular dependency, move SipTrunkCallAdapter to the Server project instead. Check `RotaryPhoneController.Core.csproj` — if it doesn't reference GVTrunk, add the reference:

```bash
dotnet add src/RotaryPhoneController.Core/RotaryPhoneController.Core.csproj reference src/RotaryPhoneController.GVTrunk/RotaryPhoneController.GVTrunk.csproj
```

**Alternative if circular:** Create `SipTrunkCallAdapter` in the Server project (`src/RotaryPhoneController.Server/Adapters/SipTrunkCallAdapter.cs`) instead. It only needs to exist where DI wiring happens.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/Adapters/SipTrunkCallAdapter.cs
git commit -m "feat: add SipTrunkCallAdapter — wraps GVTrunk as ICallAdapter"
```

---

## Chunk 4: CallManager Refactor + DI Wiring

### Task 8: Add ICallAdapterRegistry to CallManager

**Files:**
- Modify: `src/RotaryPhoneController.Core/CallManager.cs`

The key change: CallManager gets an optional `ICallAdapterRegistry` parameter. When present, it subscribes to the active adapter's events and rebinds on mode changes. The existing ISipAdapter/IBluetoothHfpAdapter/IBluetoothDeviceManager subscriptions remain for backward compatibility — the registry is additive.

- [ ] **Step 1: Add registry field and constructor parameter**

In `CallManager.cs`, add after existing fields (around line 23):

```csharp
private readonly ICallAdapterRegistry? _adapterRegistry;
private ICallAdapter? _boundAdapter;
```

Add parameter to constructor (after `IBluetoothDeviceManager? deviceManager = null`):

```csharp
ICallAdapterRegistry? adapterRegistry = null
```

Add in constructor body:

```csharp
_adapterRegistry = adapterRegistry;
```

- [ ] **Step 2: Add adapter event binding in Initialize()**

At the end of the `Initialize()` method (after the existing device manager subscriptions), add:

```csharp
// Call adapter registry (multi-mode support)
if (_adapterRegistry != null)
{
    _adapterRegistry.OnModeChanged += _ => RebindAdapterEvents();
    RebindAdapterEvents();
}
```

- [ ] **Step 3: Add RebindAdapterEvents method**

Add this method to CallManager:

```csharp
private void RebindAdapterEvents()
{
    if (_adapterRegistry == null) return;

    // Unsubscribe from previous adapter
    if (_boundAdapter != null)
    {
        _boundAdapter.OnIncomingCall -= HandleAdapterIncomingCall;
        _boundAdapter.OnCallAnswered -= HandleAdapterCallAnswered;
        _boundAdapter.OnCallEnded -= HandleAdapterCallEnded;
    }

    _boundAdapter = _adapterRegistry.ActiveAdapter;

    // Subscribe to new adapter
    _boundAdapter.OnIncomingCall += HandleAdapterIncomingCall;
    _boundAdapter.OnCallAnswered += HandleAdapterCallAnswered;
    _boundAdapter.OnCallEnded += HandleAdapterCallEnded;

    _logger.LogInformation("CallManager bound to adapter: {Mode}", _boundAdapter.Mode);
}

private void HandleAdapterIncomingCall(string phoneNumber)
{
    // Delegate to existing handler — rings the rotary phone
    HandleBluetoothIncomingCall(phoneNumber);
}

private void HandleAdapterCallAnswered()
{
    // Call was answered on the remote side (e.g., cell phone, GV browser)
    HandleCallAnsweredOnCellPhone();
}

private void HandleAdapterCallEnded()
{
    // Call ended on remote side
    if (CurrentState == CallState.Idle) return;
    HandleBluetoothCallEnded();
}
```

- [ ] **Step 4: Build and run ALL tests**

Run: `dotnet build && dotnet test --verbosity quiet`
Expected: All tests pass (the registry parameter is optional, so existing tests still work).

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Core/CallManager.cs
git commit -m "feat: add ICallAdapterRegistry support to CallManager — rebinds on mode change"
```

---

### Task 9: Update PhoneManagerService

**Files:**
- Modify: `src/RotaryPhoneController.Core/PhoneManagerService.cs`

- [ ] **Step 1: Add ICallAdapterRegistry parameter**

Add to constructor parameters (after `IBluetoothDeviceManager? deviceManager = null`):

```csharp
ICallAdapterRegistry? adapterRegistry = null
```

Store it:

```csharp
private readonly ICallAdapterRegistry? _adapterRegistry;
```

And in the `RegisterPhone` method, pass it through to the `CallManager` constructor.

- [ ] **Step 2: Build and run tests**

Run: `dotnet build && dotnet test --verbosity quiet`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/PhoneManagerService.cs
git commit -m "feat: pass ICallAdapterRegistry through PhoneManagerService to CallManager"
```

---

### Task 10: Wire up DI in Program.cs

**Files:**
- Modify: `src/RotaryPhoneController.Server/Program.cs`

- [ ] **Step 1: Register adapters and registry**

Add after the existing ISipAdapter registration (around line 200), before PhoneManagerService:

```csharp
// Call adapter registry — runtime mode switching between BT/SIP/GV
builder.Services.AddSingleton<BluetoothCallAdapter>();
builder.Services.AddSingleton<SipTrunkCallAdapter>();
builder.Services.AddSingleton<ICallAdapterRegistry>(sp =>
{
    var registry = new CallAdapterRegistry(sp.GetRequiredService<ILogger<CallAdapterRegistry>>());

    // Register Bluetooth adapter
    var btAdapter = sp.GetRequiredService<BluetoothCallAdapter>();
    registry.Register(btAdapter);

    // Register SIP Trunk adapter (if GVTrunk is configured)
    var sipAdapter = sp.GetRequiredService<SipTrunkCallAdapter>();
    registry.Register(sipAdapter);

    // Default to Bluetooth mode
    registry.SwitchModeAsync(CallAdapterMode.BluetoothHfp).GetAwaiter().GetResult();

    return registry;
});
```

Add the required using statements:

```csharp
using RotaryPhoneController.Core.Adapters;
```

- [ ] **Step 2: Pass registry to PhoneManagerService**

In the PhoneManagerService factory (around line 90-113), add the `adapterRegistry` parameter:

```csharp
var adapterRegistry = sp.GetRequiredService<ICallAdapterRegistry>();

return new PhoneManagerService(
    logger,
    config,
    sipAdapter,
    bluetoothAdapter,
    rtpBridge,
    callManagerLogger,
    callHistoryService,
    deviceManager,
    adapterRegistry);
```

- [ ] **Step 3: Build and run ALL tests**

Run: `dotnet build && dotnet test --verbosity quiet`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Server/Program.cs
git commit -m "feat: wire CallAdapterRegistry into DI — BT and SIP adapters registered"
```

---

## Chunk 5: Final Verification

### Task 11: Run full test suite and verify

- [ ] **Step 1: Run all tests**

Run: `dotnet test --verbosity quiet`
Expected: All tests pass across all test projects.

- [ ] **Step 2: Verify build for Linux**

Run: `dotnet publish src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj --configuration Release --runtime linux-x64 -f net10.0 --self-contained -v quiet`
Expected: Build succeeded.

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "feat: Phase A complete — ICallAdapter, ICallAdapterRegistry, adapter wrappers, CallManager refactor"
```

---

## What Phase A Enables

After Phase A, the system supports:
1. Runtime switching between Bluetooth and SIP Trunk modes
2. Future GVBrowser mode (Phase B adds the adapter, registered in the same registry)
3. Persisted mode selection (Phase B adds persistence)
4. Connection Mode Selector UI (Phase D uses the registry's API)

**Next phases:**
- **Phase B:** GVBridge backend (WebSocket server, AudioBridge, GVBrowserAdapter)
- **Phase C:** Chrome Extension (service worker, content script, offscreen audio)
- **Phase D:** UI (React + Blazor dashboards, ConnectionModeSelector)
