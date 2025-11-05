# Bluetooth HFP Implementation Guide

## Current Status

The system currently has **mock implementations** for Bluetooth HFP and RTP audio bridging. All interfaces are designed and ready for actual implementation.

## Implementing Actual Bluetooth HFP

### Recommended Approach: BlueZ D-Bus API

For Linux-based systems (Raspberry Pi), the recommended approach is to use the **BlueZ Bluetooth stack** via D-Bus API.

### Why BlueZ?

- Native to Linux
- Full HFP (Hands-Free Profile) support
- Well-documented D-Bus API
- Used by most Linux Bluetooth applications

### Implementation Steps

#### 1. Prerequisites

```bash
# Install BlueZ and dependencies
sudo apt-get update
sudo apt-get install bluez bluez-tools

# Install D-Bus development libraries
sudo apt-get install libdbus-1-dev
```

#### 2. NuGet Packages

Add to `RotaryPhoneController.Core.csproj`:

```xml
<PackageReference Include="Tmds.DBus" Version="0.15.0" />
```

The `Tmds.DBus` library provides .NET bindings for D-Bus, making it easy to interact with BlueZ.

#### 3. Implementation Structure

Create a new `BlueZHfpAdapter.cs` that implements `IBluetoothHfpAdapter`:

```csharp
namespace RotaryPhoneController.Core.Audio;

public class BlueZHfpAdapter : IBluetoothHfpAdapter
{
    // Connect to BlueZ via D-Bus
    // Monitor HFP events
    // Handle call state changes
    // Implement audio routing
}
```

#### 4. Key D-Bus Objects to Use

- **org.bluez.Device1**: Represents paired Bluetooth devices
- **org.bluez.MediaControl1**: Control media playback (for audio)
- **org.bluez.MediaTransport1**: Represents audio transport
- **org.bluez.Telephony**: HFP telephony interface

#### 5. Audio Routing Implementation

For audio routing, you'll need to:

1. **Detect call answer location**:
   - Hook state from HT801 → Answered on rotary phone
   - BlueZ HFP event → Answered on cell phone

2. **Route audio accordingly**:
   - **Rotary phone**: Bridge RTP ↔ Rotary handset
   - **Cell phone**: Bridge RTP ↔ Bluetooth HFP audio

3. **Use PulseAudio/PipeWire** for audio routing:
   ```bash
   # Set Bluetooth device as audio sink/source
   pactl set-default-sink bluez_sink.XX_XX_XX_XX_XX_XX.handsfree_head_unit
   pactl set-default-source bluez_source.XX_XX_XX_XX_XX_XX.handsfree_head_unit
   ```

#### 6. Example BlueZ D-Bus Code Structure

```csharp
using Tmds.DBus;

// Connect to system D-Bus
var connection = Connection.System;

// Get BlueZ adapter
var adapter = connection.CreateProxy<IAdapter1>("org.bluez", "/org/bluez/hci0");

// Monitor HFP events
connection.RegisterSignalHandler(
    "org.bluez",
    new ObjectPath("/"),
    "org.freedesktop.DBus.Properties",
    "PropertiesChanged",
    (SignalMessage signal) => {
        // Handle property changes (e.g., call state)
    });
```

## Implementing Actual RTP Audio Bridge

### Recommended Approach: SIPSorcery RTP + NAudio

The `SIPSorcery` library (already in use) provides RTP functionality. Combine with `NAudio` for audio processing.

### Implementation Steps

#### 1. Add NAudio Package

```xml
<PackageReference Include="NAudio" Version="2.2.1" />
```

#### 2. Implementation Structure

Create a new `RtpAudioBridge.cs` that implements `IRtpAudioBridge`:

```csharp
namespace RotaryPhoneController.Core.Audio;

public class RtpAudioBridge : IRtpAudioBridge
{
    // Create RTP session with HT801
    // Decode G.711 PCMU audio from RTP
    // Encode audio to RTP
    // Route to/from Bluetooth based on AudioRoute
}
```

#### 3. Audio Flow

```
Rotary Handset → HT801 → RTP (G.711) → Bridge → Bluetooth → Mobile
Mobile → Bluetooth → Bridge → RTP (G.711) → HT801 → Rotary Handset
```

#### 4. Key Components

- **RTP Session**: Use `SIPSorcery.Net.RTPSession`
- **Audio Codec**: G.711 PCMU (already in SDP)
- **Audio Routing**: Based on `AudioRoute` enum
- **Buffer Management**: Handle audio buffers for smooth playback

## Testing

### Unit Testing Mock Implementations

The mock implementations can be tested independently:

```csharp
var mockHfp = new MockBluetoothHfpAdapter(logger);
mockHfp.SimulateIncomingCall("1234567890");
mockHfp.SimulateCallAnsweredOnCellPhone();
```

### Integration Testing

Once actual implementations are ready:

1. Test with real Bluetooth device
2. Test with real HT801 and rotary phone
3. Test audio quality in both routing modes
4. Test handoff scenarios (answer on different device)

## Resources

### BlueZ Documentation
- [BlueZ D-Bus API](https://git.kernel.org/pub/scm/bluetooth/bluez.git/tree/doc)
- [HFP Profile Specification](https://www.bluetooth.com/specifications/specs/hands-free-profile-1-8/)

### Libraries
- [Tmds.DBus](https://github.com/tmds/Tmds.DBus) - .NET D-Bus library
- [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) - SIP/RTP library
- [NAudio](https://github.com/naudio/NAudio) - Audio processing library

### Example Projects
- Linux Bluetooth HFP applications
- VoIP bridging projects
- PulseAudio routing examples

## Notes

- The current architecture is designed to make the transition to actual implementations smooth
- All interfaces are in place and tested with mocks
- Audio routing logic is complete and tested
- The main work is integrating with BlueZ D-Bus and implementing RTP/audio processing
- All automatic routing requirements are already implemented in the CallManager
