# Bluetooth HFP Implementation Guide

## Current Status

The system now has **actual implementations** for Bluetooth HFP and RTP audio bridging using BlueZ and NAudio. The implementations can be enabled via configuration flags.

## Implementation Complete ✅

### BlueZ HFP Adapter
**File:** `src/RotaryPhoneController.Core/Audio/BlueZHfpAdapter.cs`

**Features:**
- ✅ Bluetooth device advertised name: "Rotary Phone" (configurable)
- ✅ Multi-device pairing support (at least 2 phones can pair)
- ✅ BlueZ integration via bluetoothctl commands
- ✅ HFP call control (initiate, answer, terminate)
- ✅ Audio routing based on where call is answered
- ✅ Device connection monitoring
- ✅ AT command support for call control

**Configuration:**
```json
{
  "BluetoothDeviceName": "Rotary Phone",
  "UseActualBluetoothHfp": true
}
```

### RTP Audio Bridge
**File:** `src/RotaryPhoneController.Core/Audio/RtpAudioBridge.cs`

**Features:**
- ✅ G.711 PCMU codec support (8kHz, 16-bit, mono)
- ✅ Bidirectional audio streaming
- ✅ RTP session management via SIPSorcery
- ✅ Audio capture and playback via NAudio
- ✅ Dynamic audio routing (rotary phone or cell phone)
- ✅ Buffer management for smooth playback

**Configuration:**
```json
{
  "UseActualRtpAudioBridge": true
}
```

## Using the Implementations

## Using the Implementations

### Step 1: Install Prerequisites

```bash
# Ensure BlueZ is installed (usually pre-installed on Raspberry Pi OS)
sudo apt-get update
sudo apt-get install bluez bluez-tools

# Verify bluetoothctl is available
which bluetoothctl
```

### Step 2: Enable Implementations

Edit `appsettings.json`:

```json
{
  "RotaryPhone": {
    "BluetoothDeviceName": "Rotary Phone",
    "UseActualBluetoothHfp": true,
    "UseActualRtpAudioBridge": true,
    ...
  }
}
```

### Step 3: Configure Bluetooth

The BlueZHfpAdapter will automatically:
- Set the Bluetooth device name to "Rotary Phone"
- Enable discoverability (always discoverable)
- Enable pairing (always pairable)
- Support multiple paired devices (at least 2)

To manually pair phones:
```bash
bluetoothctl
power on
agent on
default-agent
discoverable on
pairable on
# Then pair from your mobile phone
```

### Step 4: Run the Application

```bash
cd src/RotaryPhoneController.WebUI
dotnet run
```

The Bluetooth device will advertise as "Rotary Phone" and can be paired with multiple phones.

## Architecture Details

### BlueZ Integration

The `BlueZHfpAdapter` uses:
1. **bluetoothctl** commands for adapter configuration
2. **AT commands** for HFP call control (ATD, ATA, AT+CHUP)
3. **System monitoring** for device connections
4. **Event-driven architecture** for call state changes

### Audio Flow

```
Mobile Phone (Bluetooth HFP)
    ↕
BlueZ Stack
    ↕
BlueZHfpAdapter (Call Control)
    ↕
RtpAudioBridge (Audio Processing)
    ↕ G.711 PCMU
RTP Session
    ↕
HT801 ATA
    ↕
Rotary Phone
```

### Audio Routing Logic

**When call answered on rotary phone (handset lifted):**
```csharp
AudioRoute.RotaryPhone
→ RTP audio flows to/from HT801 (rotary handset)
→ Bluetooth captures audio for mobile network
```

**When call answered on cell phone:**
```csharp
AudioRoute.CellPhone  
→ RTP audio captured and sent to Bluetooth
→ Bluetooth audio routed to mobile device
```

## Multi-Device Pairing

The BlueZ adapter supports multiple paired devices:
- **Pairable**: Always enabled (timeout = 0)
- **Discoverable**: Always enabled (timeout = 0)
- **Connection Limit**: No artificial limit (BlueZ handles multiple connections)
- **Device Name**: "Rotary Phone" (visible to all pairing devices)

To pair a second or third phone:
1. Keep the first phone paired
2. Start pairing process on the second phone
3. The device will appear as "Rotary Phone"
4. Complete pairing - both phones can now connect

## Recommended Approach: BlueZ D-Bus API (Future Enhancement)

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
